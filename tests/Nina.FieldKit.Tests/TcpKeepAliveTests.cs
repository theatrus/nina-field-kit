using System.Net.Http;
using System.Net.Sockets;
using Nina.FieldKit.Core.Safety;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class TcpKeepAliveTests {
    [Fact] public async Task PoolSocketHasKeepAliveAndIsReusedThenDisposed() {
        await using var server = new LocalAlpacaServer();
        var options = new SafetyEndpointOptions { BaseUrl = server.BaseUrl };
        using var handler = AlpacaSafetyClient.CreateHandler(options);
        Assert.Equal(TimeSpan.FromMinutes(30), handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromSeconds(30), handler.PooledConnectionIdleTimeout);
        var connect = handler.ConnectCallback!;
        Socket? socket = null;
        var connections = 0;
        handler.ConnectCallback = async (context, token) => {
            var stream = Assert.IsType<NetworkStream>(await connect(context, token));
            socket = stream.Socket;
            connections++;
            Assert.Equal(1, (int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive)!);
            Assert.Equal(15, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime)!);
            Assert.Equal(5, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval)!);
            Assert.Equal(3, (int)socket.GetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount)!);
            return stream;
        };
        using var client = new AlpacaSafetyClient(options, handler: handler);
        Assert.Equal(PollOutcome.Observation, (await client.PollAsync(default)).Outcome);
        Assert.Equal(PollOutcome.Observation, (await client.PollAsync(default)).Outcome);
        Assert.Equal(1, connections);
        Assert.NotNull(socket);
        client.Dispose();
        Assert.True(socket.SafeHandle.IsClosed);
    }
}
