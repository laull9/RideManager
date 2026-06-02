namespace RideManager.Utils;

/// <summary>
/// Builds HttpListener endpoints that work across desktop and Linux hosts.
/// </summary>
internal static class HttpListenerEndpoint
{
    /// <summary>
    /// Returns a wildcard prefix for listening on every local interface.
    /// </summary>
    public static string CreateListenPrefix(int port)
    {
        return $"http://*:{port}/";
    }

    /// <summary>
    /// Returns a browser-friendly URL for opening the local preview.
    /// </summary>
    public static string CreateDisplayUrl(int port)
    {
        return $"http://localhost:{port}/";
    }
}
