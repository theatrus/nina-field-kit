using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Nina.FieldKit.Tests;

// A real HTTP/1.1 loopback fixture: persistent TCP sockets, no external network or hardware.
internal sealed class LocalAlpacaServer : IAsyncDisposable {
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentBag<TcpClient> clients = new();
    private readonly ConcurrentBag<Task> handlers = new();
    private readonly Task acceptLoop;
    private int connections, safetyRequests;
    public string BaseUrl { get; }
    public int Connections => Volatile.Read(ref connections);
    public int SafetyRequests => Volatile.Read(ref safetyRequests);
    public Func<int, (int Status, object Value, string? ExtraHeaders)> SafetyResponse { get; set; } = _ => (200, true, null);

    public LocalAlpacaServer() {
        listener.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/proxy/";
        acceptLoop = AcceptAsync();
    }

    private async Task AcceptAsync() {
        try {
            while (!cancellation.IsCancellationRequested) {
                var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                clients.Add(client);
                Interlocked.Increment(ref connections);
                handlers.Add(HandleAsync(client));
            }
        } catch (OperationCanceledException) { }
        catch (SocketException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client) {
        try {
            using (client) {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
                while (!cancellation.IsCancellationRequested) {
                    var line = await reader.ReadLineAsync(cancellation.Token);
                    if (line is null) return;
                    var first = line.Split(' ');
                    var length = 0;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellation.Token)))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Split(':')[1]);
                    var bodyChars = new char[length];
                    if (length > 0) await reader.ReadBlockAsync(bodyChars, cancellation.Token);
                    var uri = new Uri("http://localhost" + first[1]);
                    var parameters = (first[0] == "GET" ? uri.Query.TrimStart('?') : new string(bodyChars)).Split('&')
                        .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
                    var id = uint.Parse(parameters["ClientTransactionID"]);
                    var safety = uri.AbsolutePath.EndsWith("issafe");
                    var reply = safety ? SafetyResponse(Interlocked.Increment(ref safetyRequests)) : (200, (object)(uri.AbsolutePath.EndsWith("interfaceversion") ? 1 : true), (string?)null);
                    var bytes = Encoding.UTF8.GetBytes(reply.Item1 == 200
                        ? JsonSerializer.Serialize(new { ClientTransactionID = id, ServerTransactionID = id,
                            ErrorNumber = 0, ErrorMessage = "", Value = reply.Item2 }) : "<html>temporary upstream error</html>");
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {reply.Item1} Test\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: keep-alive\r\n{reply.Item3}\r\n");
                    await stream.WriteAsync(header, cancellation.Token);
                    await stream.WriteAsync(bytes, cancellation.Token);
                    await stream.FlushAsync(cancellation.Token);
                }
            }
        } catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync() {
        cancellation.Cancel();
        listener.Stop();
        foreach (var client in clients) client.Dispose();
        await acceptLoop;
        await Task.WhenAll(handlers).WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Dispose();
    }
}
