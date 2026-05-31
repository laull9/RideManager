namespace RideManager.Sensors;

/// <summary>
/// 保存雷达 live 测试的内存历史数据。
/// </summary>
public sealed class RadarHistory
{
    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly Queue<RadarFrame> _frames;

    /// <summary>
    /// 创建固定容量历史队列。
    /// </summary>
    public RadarHistory(int capacity)
    {
        _capacity = capacity;
        _frames = new Queue<RadarFrame>(capacity);
    }

    /// <summary>
    /// 添加一帧历史数据。
    /// </summary>
    public void Add(RadarFrame frame)
    {
        lock (_sync)
        {
            _frames.Enqueue(frame);
            while (_frames.Count > _capacity)
            {
                _frames.Dequeue();
            }
        }
    }

    /// <summary>
    /// 获取当前历史快照。
    /// </summary>
    public IReadOnlyList<RadarFrame> Snapshot()
    {
        lock (_sync)
        {
            return _frames.ToArray();
        }
    }
}
