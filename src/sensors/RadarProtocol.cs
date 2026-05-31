using System.Text.Json;

namespace RideManager.Sensors;

/// <summary>
/// EV-ADS BLE 雷达协议解析逻辑。
/// </summary>
public static class RadarProtocol
{
    /// <summary>
    /// 解析雷达数据 JSON。
    /// </summary>
    public static RadarFrame ParseFrame(ReadOnlySpan<byte> payload, DateTimeOffset observedAt)
    {
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        var status = ReadByte(root, "st");

        return new RadarFrame(
            ReadInt(root, "v"),
            ReadUInt(root, "seq"),
            ReadUInt(root, "t"),
            ReadNullableDouble(root, "br"),
            ReadNullableDouble(root, "hr"),
            ReadNullableDouble(root, "d"),
            status,
            observedAt,
            (status & RadarStatusFlags.Breath) != 0,
            (status & RadarStatusFlags.Heart) != 0,
            (status & RadarStatusFlags.Distance) != 0,
            (status & RadarStatusFlags.Presence) != 0);
    }

    /// <summary>
    /// 解析雷达健康状态 JSON。
    /// </summary>
    public static RadarHealth ParseHealth(ReadOnlySpan<byte> payload, DateTimeOffset observedAt)
    {
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;

        return new RadarHealth(
            ReadInt(root, "v"),
            ReadUInt(root, "up"),
            ReadUInt(root, "nt"),
            ReadUInt(root, "nd"),
            ReadUInt(root, "rs"),
            ReadByte(root, "cn") != 0,
            ReadString(root, "fw"),
            observedAt);
    }

    /// <summary>
    /// 判断配置地址是否为占位地址。
    /// </summary>
    public static bool IsPlaceholderAddress(string? address)
    {
        return string.IsNullOrWhiteSpace(address)
            || address.Equals("00:00:00:00:00:00", StringComparison.OrdinalIgnoreCase)
            || address.Equals("auto", StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadInt(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    }

    private static uint ReadUInt(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.TryGetUInt32(out var result) ? result : 0;
    }

    private static byte ReadByte(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.TryGetByte(out var result) ? result : (byte)0;
    }

    private static double? ReadNullableDouble(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : null;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) ? value.GetString() : null;
    }
}
