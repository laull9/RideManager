using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RideManager.Utils;

namespace RideManager.AppSync;

/// <summary>
/// 提供手机 App 同步协议 JSON 序列化工具。
/// </summary>
internal static class AppSyncJson
{
    /// <summary>
    /// 协议统一 JSON 选项。
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        TypeInfoResolver = RideManagerJsonContext.Default
    };

    /// <summary>
    /// 将任意响应负载转换为可嵌入响应信封的 JsonElement。
    /// </summary>
    public static JsonElement ToElement<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value, GetTypeInfo<T>());
    }

    /// <summary>
    /// 获取 AppSync payload 的 source-generated JSON 类型信息。
    /// </summary>
    private static JsonTypeInfo<T> GetTypeInfo<T>()
    {
        var typeInfo = RideManagerJsonContext.Default.GetTypeInfo(typeof(T));
        return typeInfo is JsonTypeInfo<T> typed
            ? typed
            : throw new InvalidOperationException($"No JSON source generation metadata registered for {typeof(T)}.");
    }

    /// <summary>
    /// 从 JSON 文档中读取字符串属性。
    /// </summary>
    public static string? GetString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    /// <summary>
    /// 从 JSON 文档中读取整数属性。
    /// </summary>
    public static int GetInt(JsonElement element, string name, int fallback)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.TryGetInt32(out var value)
                ? value
                : fallback;
    }

    /// <summary>
    /// 从 JSON 文档中读取浮点属性。
    /// </summary>
    public static double GetDouble(JsonElement element, string name, double fallback)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.TryGetDouble(out var value)
                ? value
                : fallback;
    }
}
