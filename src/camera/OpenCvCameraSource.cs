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
    private readonly Task _captureTask;
    private CameraFrame? _latestFrame;
    private long _droppedFrames;

    /// <summary>
    /// 创建 OpenCV 摄像头源并启动后台采集循环。
    /// </summary>
    public OpenCvCameraSource(CameraOptions options)
    {
        _options = options;
        _capture = OpenCapture(options.Device);
        ConfigureCapture(_capture, options);

        if (!_capture.IsOpened())
        {
            _capture.Dispose();
            throw new InvalidOperationException($"Failed to open camera device: {options.Device}");
        }

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
            var image = new Mat();
            if (!_capture.Read(image) || image.Empty())
            {
                image.Dispose();
                await Task.Delay(20, _stop.Token).ConfigureAwait(false);
                continue;
            }

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
        capture.Set(VideoCaptureProperties.FrameWidth, options.Width);
        capture.Set(VideoCaptureProperties.FrameHeight, options.Height);
        capture.Set(VideoCaptureProperties.Fps, options.Fps);
        capture.Set(VideoCaptureProperties.BufferSize, 1);
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
