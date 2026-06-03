namespace RideManager.Camera;

/// <summary>
/// 将单个底层摄像头源的最新帧广播给多个独立消费方。
/// </summary>
internal sealed class BroadcastCameraSource
{
    private static readonly TimeSpan EmptyReadDelay = TimeSpan.FromMilliseconds(1);

    private readonly ICameraSource _source;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pumpTask;
    private CameraFrame? _latestFrame;
    private long _sequence;
    private int _readerCount;
    private bool _disposed;

    /// <summary>
    /// 创建共享摄像头源并启动帧转发循环。
    /// </summary>
    public BroadcastCameraSource(ICameraSource source)
    {
        _source = source;
        _pumpTask = Task.Run(PumpAsync);
    }

    /// <summary>
    /// 为指定摄像头管线创建一个独立的最新帧读取器。
    /// </summary>
    public ICameraSource CreateReader(CameraId cameraId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _readerCount++;
        }

        return new Reader(this, cameraId);
    }

    /// <summary>
    /// 获取共享源和当前读取器累计的丢帧数。
    /// </summary>
    private long GetDroppedFrames(long readerDroppedFrames)
    {
        return _source.DroppedFrames + readerDroppedFrames;
    }

    /// <summary>
    /// 为单个读取器克隆其尚未消费的最新帧。
    /// </summary>
    private CameraFrame? ReadLatest(
        CameraId cameraId,
        ref long lastSequence,
        ref long droppedFrames,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_latestFrame is null || lastSequence == _sequence)
            {
                return null;
            }

            if (lastSequence > 0 && _sequence > lastSequence + 1)
            {
                droppedFrames += _sequence - lastSequence - 1;
            }

            lastSequence = _sequence;
            return new CameraFrame(cameraId, _latestFrame.CapturedAt, _latestFrame.Image.Clone());
        }
    }

    /// <summary>
    /// 持续消费底层源，并仅保留最新一帧供所有读取器克隆。
    /// </summary>
    private async Task PumpAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var frame = await _source.ReadLatestAsync(_stop.Token).ConfigureAwait(false);
            if (frame is null)
            {
                await Task.Delay(EmptyReadDelay, _stop.Token).ConfigureAwait(false);
                continue;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    frame.Dispose();
                    return;
                }

                _latestFrame?.Dispose();
                _latestFrame = frame;
                _sequence++;
            }
        }
    }

    /// <summary>
    /// 释放一个读取器；最后一个读取器负责停止并释放底层源。
    /// </summary>
    private async ValueTask ReleaseReaderAsync()
    {
        var disposeSource = false;
        lock (_gate)
        {
            _readerCount--;
            if (_readerCount == 0)
            {
                _disposed = true;
                disposeSource = true;
            }
        }

        if (!disposeSource)
        {
            return;
        }

        _stop.Cancel();
        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        lock (_gate)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
        }

        await _source.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    /// <summary>
    /// 表示共享源的单个管线读取端。
    /// </summary>
    private sealed class Reader : ICameraSource
    {
        private readonly BroadcastCameraSource _owner;
        private readonly CameraId _cameraId;
        private long _lastSequence;
        private long _droppedFrames;
        private int _disposed;

        public Reader(BroadcastCameraSource owner, CameraId cameraId)
        {
            _owner = owner;
            _cameraId = cameraId;
        }

        /// <inheritdoc />
        public long DroppedFrames => _owner.GetDroppedFrames(Interlocked.Read(ref _droppedFrames));

        /// <inheritdoc />
        public Task<CameraFrame?> ReadLatestAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var frame = _owner.ReadLatest(
                _cameraId,
                ref _lastSequence,
                ref _droppedFrames,
                cancellationToken);
            return Task.FromResult(frame);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return Interlocked.Exchange(ref _disposed, 1) == 0
                ? _owner.ReleaseReaderAsync()
                : ValueTask.CompletedTask;
        }
    }
}
