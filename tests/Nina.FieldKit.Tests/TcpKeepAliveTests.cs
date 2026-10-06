using Nina.FieldKit.Core.Safety;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class TcpKeepAliveTests {
    [Fact] public void UsesAscomTransportDefaultsWhileRetainingPoolLimits() {
        using var handler = AlpacaSafetyClient.CreateHandler(new SafetyEndpointOptions());
        // ASCOM.Alpaca leaves TCP keepalive to the framework; no socket override.
        Assert.Null(handler.ConnectCallback);
        Assert.Equal(TimeSpan.FromMinutes(30), handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromSeconds(30), handler.PooledConnectionIdleTimeout);
    }
}
