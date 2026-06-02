using RideManager.Utils;
using Xunit;

namespace RideManager.Tests;

public sealed class HttpListenerEndpointTests
{
    [Fact]
    public void CreateListenPrefix_UsesHttpListenerWildcardHost()
    {
        var prefix = HttpListenerEndpoint.CreateListenPrefix(5088);

        Assert.Equal("http://*:5088/", prefix);
    }

    [Fact]
    public void CreateDisplayUrl_UsesBrowserFriendlyLocalhost()
    {
        var url = HttpListenerEndpoint.CreateDisplayUrl(5088);

        Assert.Equal("http://localhost:5088/", url);
    }
}
