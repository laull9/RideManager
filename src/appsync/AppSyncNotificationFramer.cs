using System.Text;
using System.Globalization;

namespace RideManager.AppSync;

/// <summary>
/// 为 BLE 通知生成可重组的 AppSync 响应分片。
/// </summary>
internal static class AppSyncNotificationFramer
{
    private static long _nextMessageId;

    /// <summary>
    /// 把完整 JSON 响应封装为一组小 JSON 通知，每片都包含重组元数据。
    /// </summary>
    public static IReadOnlyList<byte[]> CreateChunks(string response, int notifyChunkBytes)
    {
        var data = Encoding.UTF8.GetBytes(response);
        var messageId = Interlocked.Increment(ref _nextMessageId).ToString();
        var maxBytes = Math.Max(64, notifyChunkBytes);
        var payloadBytes = Math.Max(1, EstimatePayloadBytes(maxBytes, messageId, data.Length, data.Length, 0, 1));

        while (payloadBytes > 0)
        {
            var chunks = BuildChunks(data, messageId, payloadBytes);
            if (chunks.All(chunk => chunk.Length <= maxBytes))
            {
                return chunks;
            }

            payloadBytes--;
        }

        return BuildChunks(data, messageId, 1);
    }

    private static IReadOnlyList<byte[]> BuildChunks(byte[] data, string messageId, int payloadBytes)
    {
        var count = Math.Max(1, (data.Length + payloadBytes - 1) / payloadBytes);
        var chunks = new List<byte[]>(count);
        for (var index = 0; index < count; index++)
        {
            var offset = index * payloadBytes;
            var length = Math.Min(payloadBytes, data.Length - offset);
            var chunkData = length <= 0
                ? string.Empty
                : Convert.ToBase64String(data, offset, length);
            chunks.Add(Encoding.UTF8.GetBytes(CreateFrame(messageId, index, count, data.Length, chunkData)));
        }

        return chunks;
    }

    private static int EstimatePayloadBytes(
        int maxBytes,
        string messageId,
        int totalBytes,
        int payloadBytes,
        int index,
        int count)
    {
        var emptyFrameBytes = Encoding.UTF8.GetByteCount(CreateFrame(messageId, index, count, totalBytes, string.Empty));
        var availableBase64Bytes = Math.Max(4, maxBytes - emptyFrameBytes);
        return Math.Min(payloadBytes, Math.Max(1, availableBase64Bytes / 4 * 3));
    }

    private static string CreateFrame(string messageId, int index, int count, int totalBytes, string data)
    {
        return "{\"v\":1,\"t\":\"chunk\",\"id\":\""
            + messageId
            + "\",\"i\":"
            + index.ToString(CultureInfo.InvariantCulture)
            + ",\"n\":"
            + count.ToString(CultureInfo.InvariantCulture)
            + ",\"b\":"
            + totalBytes.ToString(CultureInfo.InvariantCulture)
            + ",\"d\":\""
            + data
            + "\"}";
    }
}
