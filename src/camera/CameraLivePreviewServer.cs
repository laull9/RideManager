using System.Collections.Concurrent;
using System.Net;
using System.Text;
using OpenCvSharp;
using RideManager.Utils;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace RideManager.Camera;

/// <summary>
/// 提供不依赖 OpenCV HighGUI 的本地 Web 实时预览。
/// </summary>
public sealed class CameraLivePreviewServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<CameraId, byte[]> _frames = new();
    private readonly Func<IReadOnlyCollection<CameraId>> _getActiveCameras;
    private readonly Action<string> _setActiveCamera;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listenTask;

    /// <summary>
    /// 创建本地 Web 预览服务。
    /// </summary>
    public CameraLivePreviewServer(
        int port,
        Func<IReadOnlyCollection<CameraId>> getActiveCameras,
        Action<string> setActiveCamera)
    {
        Port = port;
        Url = HttpListenerEndpoint.CreateDisplayUrl(port);
        _getActiveCameras = getActiveCameras;
        _setActiveCamera = setActiveCamera;
        _listener.Prefixes.Add(HttpListenerEndpoint.CreateListenPrefix(port));
        _listener.Start();
        _listenTask = Task.Run(ListenAsync);
    }

    /// <summary>
    /// 获取预览端口。
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// 获取预览地址。
    /// </summary>
    public string Url { get; }

    /// <summary>
    /// 发布一帧带 overlay 的预览图。
    /// </summary>
    public void Publish(CameraPipelineResult result)
    {
        _frames[result.CameraId] = EncodeJpeg(result.PreviewImage);
    }

    /// <summary>
    /// 停止 Web 预览服务。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();

        try
        {
            await _listenTask.ConfigureAwait(false);
        }
        catch (HttpListenerException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _listener.Close();
        _stop.Dispose();
    }

    /// <summary>
    /// 接收并处理本地 HTTP 请求。
    /// </summary>
    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var context = await _listener.GetContextAsync().ConfigureAwait(false);
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    /// <summary>
    /// 根据路径返回页面、快照或切换动作。
    /// </summary>
    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path.Equals("/", StringComparison.OrdinalIgnoreCase))
            {
                await WriteHtmlAsync(context).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/set", StringComparison.OrdinalIgnoreCase))
            {
                _setActiveCamera(context.Request.QueryString["camera"] ?? "all");
                await WriteTextAsync(context, "ok", "text/plain").ConfigureAwait(false);
                return;
            }

            if (path.Equals("/active", StringComparison.OrdinalIgnoreCase))
            {
                var active = string.Join(',', _getActiveCameras());
                await WriteTextAsync(context, active, "text/plain").ConfigureAwait(false);
                return;
            }

            if (path.StartsWith("/snapshot/", StringComparison.OrdinalIgnoreCase))
            {
                await WriteSnapshotAsync(context, path).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = 404;
            context.Response.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            context.Response.Abort();
        }
    }

    /// <summary>
    /// 返回实时预览页面。
    /// </summary>
    private async Task WriteHtmlAsync(HttpListenerContext context)
    {
        var html = """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>RideManager Live Test</title>
              <style>
                body { margin: 0; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; background: #101114; color: #f4f4f5; }
                header { display: flex; align-items: center; gap: 12px; padding: 12px 16px; background: #1c1d22; position: sticky; top: 0; z-index: 1; }
                button { border: 1px solid #3f4149; color: #f4f4f5; background: #292b32; padding: 8px 12px; border-radius: 6px; cursor: pointer; }
                button:hover { background: #383b45; }
                main { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 560px), 1fr)); gap: 12px; padding: 12px; }
                main.single { grid-template-columns: minmax(0, 1fr); }
                section { background: #1c1d22; border: 1px solid #2f3037; border-radius: 8px; overflow: hidden; }
                section.hidden { display: none; }
                h2 { font-size: 14px; font-weight: 600; margin: 0; padding: 10px 12px; border-bottom: 1px solid #2f3037; }
                img { display: block; width: 100%; height: min(72vh, 720px); object-fit: contain; background: #07080a; }
                main.single img { height: calc(100vh - 78px); }
              </style>
            </head>
            <body>
              <header>
                <strong>RideManager Live Test</strong>
                <button onclick="setCamera('front')">Front</button>
                <button onclick="setCamera('face')">Face</button>
                <button onclick="setCamera('back')">Back</button>
                <button onclick="setCamera('all')">All</button>
              </header>
              <main id="grid">
                <section data-camera="CamFront"><h2>Front</h2><img id="CamFront"></section>
                <section data-camera="CamFace"><h2>Face</h2><img id="CamFace"></section>
                <section data-camera="CamBack"><h2>Back</h2><img id="CamBack"></section>
              </main>
              <script>
                const cameras = ["CamFront", "CamFace", "CamBack"];
                const grid = document.getElementById("grid");
                async function setCamera(camera) { await fetch(`/set?camera=${camera}`, { cache: "no-store" }); }
                async function refresh() {
                  const stamp = Date.now();
                  const activeText = await fetch("/active", { cache: "no-store" }).then(response => response.text());
                  const active = new Set(activeText.split(",").filter(Boolean));
                  grid.classList.toggle("single", active.size === 1);
                  for (const section of document.querySelectorAll("section[data-camera]")) {
                    section.classList.toggle("hidden", !active.has(section.dataset.camera));
                  }
                  for (const camera of cameras) {
                    if (!active.has(camera)) continue;
                    document.getElementById(camera).src = `/snapshot/${camera}.jpg?t=${stamp}`;
                  }
                }
                setInterval(refresh, 120);
                refresh();
              </script>
            </body>
            </html>
            """;

        await WriteTextAsync(context, html, "text/html; charset=utf-8").ConfigureAwait(false);
    }

    /// <summary>
    /// 返回指定摄像头的最新 JPEG 快照。
    /// </summary>
    private async Task WriteSnapshotAsync(HttpListenerContext context, string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!Enum.TryParse<CameraId>(name, out var cameraId)
            || !_frames.TryGetValue(cameraId, out var frame))
        {
            context.Response.StatusCode = 204;
            context.Response.Close();
            return;
        }

        context.Response.ContentType = "image/jpeg";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = frame.Length;
        await context.Response.OutputStream.WriteAsync(frame).ConfigureAwait(false);
        context.Response.Close();
    }

    /// <summary>
    /// 返回文本响应。
    /// </summary>
    private static async Task WriteTextAsync(HttpListenerContext context, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    /// <summary>
    /// 使用纯托管编码器把 OpenCV BGR 图像转换为 JPEG。
    /// </summary>
    private static unsafe byte[] EncodeJpeg(Mat bgrImage)
    {
        var width = bgrImage.Width;
        var height = bgrImage.Height;
        using var image = new Image<Rgb24>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var source = (byte*)bgrImage.Ptr(y);
                var target = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var sourceIndex = x * 3;
                    target[x] = new Rgb24(
                        source[sourceIndex + 2],
                        source[sourceIndex + 1],
                        source[sourceIndex]);
                }
            }
        });

        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream, new JpegEncoder { Quality = 82 });
        return stream.ToArray();
    }
}
