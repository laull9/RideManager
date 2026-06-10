using System.Globalization;
using System.Text;

namespace RideManager.AppSync;

/// <summary>
/// 提供同步分页游标编码。
/// </summary>
public sealed record AppSyncCursor(DateTimeOffset DecidedAt, Guid Id)
{
    /// <summary>
    /// 将游标编码为 URL 安全字符串。
    /// </summary>
    public string Encode()
    {
        var raw = string.Create(
            CultureInfo.InvariantCulture,
            $"{DecidedAt.UtcDateTime:O}|{Id:D}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// 尝试解析同步游标。
    /// </summary>
    public static bool TryDecode(string? value, out AppSyncCursor cursor)
    {
        cursor = new AppSyncCursor(DateTimeOffset.MinValue, Guid.Empty);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var parts = raw.Split('|', 2);
            if (parts.Length != 2
                || !DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var decidedAt)
                || !Guid.TryParse(parts[1], out var id))
            {
                return false;
            }

            cursor = new AppSyncCursor(decidedAt.ToUniversalTime(), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
