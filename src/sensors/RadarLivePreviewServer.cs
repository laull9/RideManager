using System.Net;
using System.Text;
using System.Text.Json;
using RideManager.Utils;

namespace RideManager.Sensors;

/// <summary>
/// 提供雷达 live 测试浏览器曲线页面。
/// </summary>
public sealed class RadarLivePreviewServer : IAsyncDisposable
{
    private readonly Func<RadarLiveState> _getState;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listenTask;

    /// <summary>
    /// 创建雷达本地 Web 服务。
    /// </summary>
    public RadarLivePreviewServer(int port, Func<RadarLiveState> getState)
    {
        Port = port;
        Url = HttpListenerEndpoint.CreateDisplayUrl(port);
        _getState = getState;
        _listener.Prefixes.Add(HttpListenerEndpoint.CreateListenPrefix(port));
        _listener.Start();
        _listenTask = Task.Run(ListenAsync);
    }

    /// <summary>
    /// 获取服务端口。
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// 获取服务地址。
    /// </summary>
    public string Url { get; }

    /// <summary>
    /// 停止 Web 服务。
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
    /// 接收本地 HTTP 请求。
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
    /// 根据路径返回页面或状态 JSON。
    /// </summary>
    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path.Equals("/", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextAsync(context, Html, "text/html; charset=utf-8").ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/state", StringComparison.OrdinalIgnoreCase))
            {
                var json = JsonSerializer.Serialize(_getState(), RideManagerJsonContext.Default.RadarLiveState);
                await WriteTextAsync(context, json, "application/json; charset=utf-8").ConfigureAwait(false);
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
    /// 返回文本响应。
    /// </summary>
    private static async Task WriteTextAsync(HttpListenerContext context, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.ContentType = contentType;
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private const string Html = """
        <!doctype html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>RideManager Radar Live</title>
          <style>
            :root { color-scheme: dark; }
            * { box-sizing: border-box; }
            body { margin: 0; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; background: #111214; color: #f5f5f4; }
            header { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 12px 16px; border-bottom: 1px solid #2f3136; background: #1b1d21; }
            h1 { margin: 0; font-size: 16px; font-weight: 650; letter-spacing: 0; }
            main { display: grid; grid-template-columns: minmax(0, 1fr) 320px; min-height: calc(100vh - 50px); }
            canvas { display: block; width: 100%; height: calc(100vh - 50px); background: #090a0c; }
            aside { border-left: 1px solid #2f3136; background: #181a1e; padding: 14px; overflow: auto; }
            dl { display: grid; grid-template-columns: 1fr 1fr; gap: 10px 12px; margin: 0; }
            dt { color: #a8adb7; font-size: 12px; }
            dd { margin: 2px 0 0; font-size: 18px; font-weight: 650; }
            .ok { color: #72d38b; }
            .warn { color: #f3be5c; }
            .muted { color: #a8adb7; }
            .legend { display: flex; flex-wrap: wrap; gap: 10px; font-size: 12px; color: #d8dadf; }
            .dot { display: inline-block; width: 10px; height: 10px; border-radius: 999px; margin-right: 5px; }
            pre { white-space: pre-wrap; word-break: break-word; color: #c8ccd4; font-size: 12px; margin: 14px 0 0; }
            @media (max-width: 820px) {
              main { grid-template-columns: 1fr; }
              canvas { height: 58vh; }
              aside { border-left: 0; border-top: 1px solid #2f3136; }
            }
          </style>
        </head>
        <body>
          <header>
            <h1>RideManager Radar Live</h1>
            <div class="legend">
              <span><i class="dot" style="background:#ef6461"></i>Heart</span>
              <span><i class="dot" style="background:#6bb7ff"></i>Breath</span>
              <span><i class="dot" style="background:#f4d35e"></i>Distance</span>
            </div>
          </header>
          <main>
            <canvas id="chart"></canvas>
            <aside>
              <dl>
                <div><dt>Status</dt><dd id="phase" class="warn">starting</dd></div>
                <div><dt>Presence</dt><dd id="presence">--</dd></div>
                <div><dt>Heart BPM</dt><dd id="hr">--</dd></div>
                <div><dt>Breath BPM</dt><dd id="br">--</dd></div>
                <div><dt>Distance cm</dt><dd id="dist">--</dd></div>
                <div><dt>Sequence</dt><dd id="seq">--</dd></div>
                <div><dt>Stale ms</dt><dd id="stale">--</dd></div>
                <div><dt>Firmware</dt><dd id="fw">--</dd></div>
              </dl>
              <pre id="detail"></pre>
            </aside>
          </main>
          <script>
            const canvas = document.getElementById("chart");
            const ctx = canvas.getContext("2d");
            const fields = {
              phase: document.getElementById("phase"),
              presence: document.getElementById("presence"),
              hr: document.getElementById("hr"),
              br: document.getElementById("br"),
              dist: document.getElementById("dist"),
              seq: document.getElementById("seq"),
              stale: document.getElementById("stale"),
              fw: document.getElementById("fw"),
              detail: document.getElementById("detail")
            };
            function resize() {
              const ratio = window.devicePixelRatio || 1;
              canvas.width = Math.floor(canvas.clientWidth * ratio);
              canvas.height = Math.floor(canvas.clientHeight * ratio);
              ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
            }
            function fmt(value, digits = 1) { return value == null ? "--" : Number(value).toFixed(digits); }
            function latestAgeMs(latest) {
              return latest ? Math.max(0, Date.now() - Date.parse(latest.observedAt)) : null;
            }
            function updateStats(state) {
              const frame = state.latestFrame;
              fields.phase.textContent = state.connection.phase;
              fields.phase.className = state.connection.phase === "connected" ? "ok" : "warn";
              fields.presence.textContent = frame ? (frame.hasPresence ? "yes" : "no") : "--";
              fields.hr.textContent = frame && frame.hasHeartRate ? fmt(frame.heartRateBpm) : "--";
              fields.br.textContent = frame && frame.hasBreathingRate ? fmt(frame.breathingRateBpm) : "--";
              fields.dist.textContent = frame && frame.hasDistance ? fmt(frame.distanceCm) : "--";
              fields.seq.textContent = frame ? frame.sequence : "--";
              fields.stale.textContent = fmt(latestAgeMs(frame), 0);
              fields.fw.textContent = state.latestHealth?.firmwareVersion || "--";
              fields.detail.textContent = `${state.connection.deviceName || ""} ${state.connection.deviceAddress || ""}\n${state.connection.message || ""}`;
            }
            function drawSeries(points, key, color, min, max) {
              const w = canvas.clientWidth;
              const h = canvas.clientHeight;
              const pad = { l: 46, r: 18, t: 20, b: 34 };
              const last = points.length ? points[points.length - 1].seconds : 60;
              const first = Math.max(0, last - 60);
              ctx.beginPath();
              let hasPoint = false;
              for (const p of points) {
                const value = p[key];
                if (value == null || p.seconds < first) continue;
                const x = pad.l + ((p.seconds - first) / Math.max(1, last - first)) * (w - pad.l - pad.r);
                const y = pad.t + (1 - (value - min) / (max - min)) * (h - pad.t - pad.b);
                if (!hasPoint) ctx.moveTo(x, y); else ctx.lineTo(x, y);
                hasPoint = true;
              }
              ctx.strokeStyle = color;
              ctx.lineWidth = 2;
              ctx.stroke();
            }
            function drawChart(state) {
              resize();
              const w = canvas.clientWidth;
              const h = canvas.clientHeight;
              ctx.clearRect(0, 0, w, h);
              ctx.fillStyle = "#090a0c";
              ctx.fillRect(0, 0, w, h);
              ctx.strokeStyle = "#2b2e34";
              ctx.lineWidth = 1;
              for (let i = 0; i <= 5; i++) {
                const y = 20 + i * ((h - 54) / 5);
                ctx.beginPath();
                ctx.moveTo(46, y);
                ctx.lineTo(w - 18, y);
                ctx.stroke();
              }
              ctx.fillStyle = "#9fa5b1";
              ctx.font = "12px -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif";
              ctx.fillText("last 60s", 46, h - 12);
              drawSeries(state.history, "heartRateBpm", "#ef6461", 45, 125);
              drawSeries(state.history, "breathingRateBpm", "#6bb7ff", 5, 30);
              drawSeries(state.history, "distanceCm", "#f4d35e", 20, 100);
            }
            async function refresh() {
              const state = await fetch("/api/state", { cache: "no-store" }).then(response => response.json());
              updateStats(state);
              drawChart(state);
            }
            window.addEventListener("resize", refresh);
            setInterval(refresh, 120);
            refresh();
          </script>
        </body>
        </html>
        """;
}
