using System.Diagnostics;
using OpenCvSharp;
using RideManager.Utils;

namespace RideManager.Camera;

/// <summary>
/// 使用 OpenCV VideoCapture 读取真实摄像头，并仅保留最新一帧。
/// </summary>
public sealed class OpenCvCameraSource : ICameraSource
{
    private readonly CameraOptions _options;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly VideoCapture _capture;
    private readonly bool _isReplaySource;
    private readonly TimeSpan _replayFrameInterval;
    private readonly Task _captureTask;
    private CameraFrame? _latestFrame;
    private long _droppedFrames;
    private long _consecutiveReadFailures;

    /// <summary>
    /// 创建 OpenCV 摄像头源并启动后台采集循环。
    /// </summary>
    public OpenCvCameraSource(CameraOptions options)
    {
        _options = options;
        _isReplaySource = IsReplaySource(options.Device);
        _capture = OpenCapture(options.Device);
        ConfigureCapture(_capture, options);

        if (!_capture.IsOpened())
        {
            _capture.Dispose();
            throw new InvalidOperationException($"Failed to open camera device: {options.Device}");
        }

        ReportCaptureConfiguration(_capture, options);
        _replayFrameInterval = GetReplayFrameInterval(_capture, options);
        _captureTask = Task.Run(CaptureLoopAsync);
    }

    /// <summary>
    /// 获取因下游未及时消费而被覆盖的帧数。
    /// </summary>
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

    /// <summary>
    /// 取出当前最新帧；取出后缓存立即清空，保证下游不处理旧帧。
    /// </summary>
    public Task<CameraFrame?> ReadLatestAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var frame = _latestFrame;
            _latestFrame = null;
            return Task.FromResult(frame);
        }
    }

    /// <summary>
    /// 停止采集并释放 OpenCV 资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();

        try
        {
            await _captureTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        lock (_gate)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
        }

        _capture.Dispose();
        _stop.Dispose();
    }

    /// <summary>
    /// 后台持续读取摄像头并覆盖旧帧。
    /// </summary>
    private async Task CaptureLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var frameWatch = Stopwatch.StartNew();
            var image = new Mat();
            if (!_capture.Read(image) || image.Empty())
            {
                image.Dispose();
                ReportReadFailure();
                if (_isReplaySource)
                {
                    LoopReplaySource();
                }

                await Task.Delay(20, _stop.Token).ConfigureAwait(false);
                continue;
            }

            Interlocked.Exchange(ref _consecutiveReadFailures, 0);
            var frame = new CameraFrame(_options.Id, DateTimeOffset.UtcNow, image);
            lock (_gate)
            {
                if (_latestFrame is not null)
                {
                    _latestFrame.Dispose();
                    Interlocked.Increment(ref _droppedFrames);
                }

                _latestFrame = frame;
            }

            await DelayReplayFrameAsync(frameWatch.Elapsed).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 根据设备配置打开摄像头编号、视频文件或流地址。
    /// </summary>
    private static VideoCapture OpenCapture(string device)
    {
        if (TryParseDeviceIndex(device, out var index))
        {
            return new VideoCapture(index);
        }

        return new VideoCapture(device);
    }

    /// <summary>
    /// 设置摄像头的分辨率、帧率和低缓冲策略。
    /// </summary>
    private static void ConfigureCapture(VideoCapture capture, CameraOptions options)
    {
        if (IsReplaySource(options.Device))
        {
            return;
        }

        if (TryGetPixelFormat(options.PixelFormat, out var pixelFormat))
        {
            capture.Set(VideoCaptureProperties.FourCC, pixelFormat);
        }

        capture.Set(VideoCaptureProperties.FrameWidth, options.Width);
        capture.Set(VideoCaptureProperties.FrameHeight, options.Height);
        capture.Set(VideoCaptureProperties.Fps, options.Fps);
        capture.Set(VideoCaptureProperties.BufferSize, 1);
    }

    /// <summary>
    /// 打印摄像头实际协商到的后端、像素格式、分辨率和帧率。
    /// </summary>
    private static void ReportCaptureConfiguration(VideoCapture capture, CameraOptions options)
    {
        if (IsReplaySource(options.Device))
        {
            return;
        }

        var backend = (VideoCaptureAPIs)(int)capture.Get(VideoCaptureProperties.Backend);
        var pixelFormat = FormatFourCc((int)capture.Get(VideoCaptureProperties.FourCC));
        var width = capture.Get(VideoCaptureProperties.FrameWidth);
        var height = capture.Get(VideoCaptureProperties.FrameHeight);
        var fps = capture.Get(VideoCaptureProperties.Fps);
        Console.WriteLine(
            $"Camera {options.Id} opened device={options.Device} backend={backend} requested_pixel_format={options.PixelFormat} pixel_format={pixelFormat} size={width:F0}x{height:F0} fps={fps:F1}");
    }

    /// <summary>
    /// 在首次和周期性读取失败时输出带摄像头标识的诊断信息。
    /// </summary>
    private void ReportReadFailure()
    {
        var failures = Interlocked.Increment(ref _consecutiveReadFailures);
        if (failures == 1 || failures % 10 == 0)
        {
            Console.WriteLine(
                $"Camera {_options.Id} read failed device={_options.Device} consecutive_failures={failures}. Check V4L2 format and USB bandwidth.");
        }
    }

    /// <summary>
    /// 文件源按原始 FPS 重放，避免 live test 把整段视频瞬间读完。
    /// </summary>
    private async Task DelayReplayFrameAsync(TimeSpan elapsed)
    {
        if (!_isReplaySource)
        {
            return;
        }

        var delay = _replayFrameInterval - elapsed;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 文件源读到结尾后回到第一帧，支持循环 live test。
    /// </summary>
    private void LoopReplaySource()
    {
        _capture.Set(VideoCaptureProperties.PosFrames, 0);
        _capture.Set(VideoCaptureProperties.PosMsec, 0);
    }

    /// <summary>
    /// 获取文件源的播放帧间隔。
    /// </summary>
    private static TimeSpan GetReplayFrameInterval(VideoCapture capture, CameraOptions options)
    {
        var fps = capture.Get(VideoCaptureProperties.Fps);
        if (double.IsNaN(fps) || fps <= 0)
        {
            fps = options.Fps;
        }

        fps = Math.Clamp(fps, 1.0, 120.0);
        return TimeSpan.FromSeconds(1.0 / fps);
    }

    /// <summary>
    /// 判断输入源是否为本地图片或视频文件。
    /// </summary>
    private static bool IsReplaySource(string device)
    {
        return !TryParseDeviceIndex(device, out _) && File.Exists(device);
    }

    /// <summary>
    /// 将配置中的四字符像素格式转换为 OpenCV FourCC；auto 表示由驱动协商。
    /// </summary>
    internal static bool TryGetPixelFormat(string? value, out int pixelFormat)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            pixelFormat = 0;
            return false;
        }

        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 4)
        {
            throw new InvalidOperationException(
                $"Camera pixel_format must be a four-character code such as MJPG or YUYV, or auto: {value}");
        }

        pixelFormat = FourCC.FromString(normalized);
        return true;
    }

    /// <summary>
    /// 将 OpenCV FourCC 整数转换为可读的四字符文本。
    /// </summary>
    internal static string FormatFourCc(int value)
    {
        if (value == 0)
        {
            return "unknown";
        }

        Span<char> chars = stackalloc char[4];
        for (var index = 0; index < chars.Length; index++)
        {
            var character = (char)((value >> (index * 8)) & 0xff);
            chars[index] = char.IsControl(character) ? '?' : character;
        }

        return new string(chars);
    }

    /// <summary>
    /// 支持 "0" 和 "/dev/video0" 两种设备编号写法。
    /// </summary>
    private static bool TryParseDeviceIndex(string device, out int index)
    {
        if (int.TryParse(device, out index))
        {
            return true;
        }

        const string linuxPrefix = "/dev/video";
        if (device.StartsWith(linuxPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(device[linuxPrefix.Length..], out index))
        {
            return true;
        }

        index = 0;
        return false;
    }
}
