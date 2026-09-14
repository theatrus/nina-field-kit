using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Nina.FieldKit.Testing;

public sealed record SeenRequest(long Id, long Connection, int Device, string Method, string Member, uint Transaction,
    string? UserAgent, int Revision, int Ordinal, double ElapsedSeconds);

public sealed class FaultServer : IAsyncDisposable {
    private readonly object gate = new();
    private TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<long, TcpClient> clients = new();
    private readonly ConcurrentDictionary<long, Task> handlers = new();
    private readonly ConcurrentQueue<SeenRequest> requests = new();
    private readonly ConcurrentQueue<Exception> errors = new();
    private readonly Dictionary<int, bool> connected;
    private readonly Dictionary<FaultRule, int> ordinals = new();
    private readonly X509Certificate2? certificate;
    private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
    private readonly string prefix;
    private Task acceptLoop;
    private bool active, listening = true;
    private int revision;
    private long nextConnection, nextRequest, totalRequests;
    private int disposed;
    public FaultScenario Scenario { get; }
    public RunJournal Journal { get; }
    public string BaseUrl { get; }
    public int Port { get; }
    public CancellationToken Lifetime => stop.Token;
    public long Connections => Interlocked.Read(ref nextConnection);
    public long RequestCount => Interlocked.Read(ref totalRequests);
    public SeenRequest[] Requests => requests.ToArray();
    public Exception[] Errors => errors.ToArray();
    public object Status { get { lock (gate) return new { Scenario.Id, active, revision, listening, BaseUrl, connections = Connections, requests = RequestCount, errors = Errors.Length }; } }

    public FaultServer(FaultScenario scenario, int port = 0, string basePath = "/", RunJournal? journal = null, X509Certificate2? certificate = null) {
        scenario.Validate();
        // Freeze caller-owned dictionaries/arrays: activation must not mutate an accepted response plan.
        Scenario = JsonSerializer.Deserialize<FaultScenario>(JsonSerializer.Serialize(scenario, FaultScenario.Json), FaultScenario.Json)!;
        if (!basePath.StartsWith('/') || !basePath.EndsWith('/') || basePath.Contains("..") || basePath.Contains('?')) throw new ArgumentException("Invalid base path");
        prefix = basePath + "api/v1/safetymonitor/";
        this.certificate = certificate;
        Journal = journal ?? new();
        connected = Scenario.Devices.ToDictionary(d => d.Number, d => d.Connected);
        active = !Scenario.Armed;
        listener = new(IPAddress.Loopback, port); listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        BaseUrl = $"{(certificate is null ? "http" : "https")}://127.0.0.1:{Port}{basePath}";
        stop.CancelAfter(TimeSpan.FromSeconds(Scenario.MaximumRunSeconds));
        Journal.Write("Ready", new { Scenario.Id, BaseUrl, active, Scenario.MaximumRunSeconds });
        acceptLoop = AcceptAsync();
    }
    public void Activate() { lock (gate) { ordinals.Clear(); active = true; revision++; Journal.Write("Activated", new { revision }); } }
    public void Reset() { lock (gate) { active = false; ordinals.Clear(); revision++; foreach (var d in Scenario.Devices) connected[d.Number] = d.Connected; Journal.Write("Reset", new { revision }); } }
    public async Task PauseAsync() {
        lock (gate) { listening = false; listener.Stop(); }
        await acceptLoop.ConfigureAwait(false);
        foreach (var client in clients.Values) client.Dispose();
        Journal.Write("ListenerPaused", new { });
    }
    public void Resume() {
        lock (gate) { if (listening || stop.IsCancellationRequested) return; listener = new(IPAddress.Loopback, Port); listener.Start(); listening = true; acceptLoop = AcceptAsync(); }
        Journal.Write("ListenerResumed", new { });
    }
    private async Task AcceptAsync() {
        try {
            while (!stop.IsCancellationRequested) {
                var client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                var id = Interlocked.Increment(ref nextConnection);
                if (clients.Count >= 64) { client.Dispose(); Journal.Write("ConnectionLimit", new { id }); continue; }
                clients[id] = client;
                var task = HandleAsync(id, client);
                handlers[id] = task;
                _ = task.ContinueWith(_ => handlers.TryRemove(id, out var ignored), TaskScheduler.Default);
            }
        } catch (Exception e) when ((e is OperationCanceledException or SocketException or ObjectDisposedException) && (!listening || stop.IsCancellationRequested)) { }
        catch (Exception e) { errors.Enqueue(e); Journal.Write("ServerError", new { type = e.GetType().Name }); }
    }
    private async Task HandleAsync(long connection, TcpClient client) {
        var bytesSent = 0L;
        try {
            using (client) {
                Stream stream = client.GetStream();
                if (certificate is not null) {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false); stream = ssl;
                    using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, handshakeTimeout.Token).ConfigureAwait(false);
                }
                using (stream) {
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    while (!stop.IsCancellationRequested) {
                        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        readTimeout.CancelAfter(TimeSpan.FromSeconds(35));
                        var line = await ReadLine(reader, readTimeout.Token).ConfigureAwait(false);
                        if (line is null) return;
                        var parts = line.Split(' ');
                        if (parts.Length != 3) throw new InvalidDataException("Invalid request line");
                        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        var size = line.Length;
                        while (!string.IsNullOrEmpty(line = await ReadLine(reader, readTimeout.Token).ConfigureAwait(false))) {
                            size += line.Length;
                            if (size > 16384 || !line.Contains(':')) throw new InvalidDataException("Invalid request headers");
                            var pair = line.Split(':', 2); headers[pair[0]] = pair[1].Trim();
                        }
                        var length = headers.TryGetValue("Content-Length", out var contentLength) ? int.Parse(contentLength, CultureInfo.InvariantCulture) : 0;
                        if (length is < 0 or > 4096) throw new InvalidDataException("Request body too large");
                        var body = new char[length];
                        if (length > 0 && await reader.ReadBlockAsync(body, readTimeout.Token).ConfigureAwait(false) != length) return;
                        var uri = new Uri("http://localhost" + parts[1]);
                        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Unknown route prefix");
                        var route = uri.AbsolutePath[prefix.Length..].Split('/');
                        if (route.Length != 2 || !int.TryParse(route[0], out var device)) throw new InvalidDataException("Invalid device route");
                        var parameters = (parts[0] == "GET" ? uri.Query.TrimStart('?') : new string(body)).Split('&', StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");
                        var transaction = uint.Parse(parameters["ClientTransactionID"], CultureInfo.InvariantCulture);
                        FaultReply reply; int ordinal; int currentRevision;
                        lock (gate) {
                            currentRevision = revision;
                            (reply, ordinal) = Plan(device, parts[0], route[1], parameters);
                        }
                        var request = new SeenRequest(Interlocked.Increment(ref nextRequest), connection, device, parts[0], route[1], transaction,
                            headers.GetValueOrDefault("User-Agent"), currentRevision, ordinal, elapsed.Elapsed.TotalSeconds);
                        requests.Enqueue(request); Interlocked.Increment(ref totalRequests);
                        if (requests.Count > 10000) requests.TryDequeue(out _);
                        Journal.Write("Request", request);
                        if (reply.HeaderDelayMs > 0) await Task.Delay(reply.HeaderDelayMs, stop.Token).ConfigureAwait(false);
                        if (reply.Kind == ReplyKind.Stall) await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
                        if (reply.Kind == ReplyKind.Reset) { client.Client.LingerState = new(true, 0); return; }
                        if (reply.Kind == ReplyKind.Close) return;
                        async Task Write(byte[] data) { await stream.WriteAsync(data, stop.Token).ConfigureAwait(false); bytesSent += data.Length; }
                        if (reply.RawHttp is not null) {
                            await Write(Encoding.UTF8.GetBytes(Expand(reply.RawHttp, transaction, request.Id)));
                            return;
                        }
                        var payload = Payload(reply, transaction, request.Id);
                        var header = new StringBuilder($"HTTP/1.1 {reply.Status} Test\r\nContent-Type: application/json\r\n");
                        header.Append(reply.Chunked ? "Transfer-Encoding: chunked\r\n" : $"Content-Length: {payload.Length}\r\n");
                        header.Append($"Connection: {(reply.CloseAfter ? "close" : "keep-alive")}\r\n");
                        foreach (var pair in reply.Headers) header.Append($"{pair.Key}: {pair.Value}\r\n");
                        header.Append("\r\n"); await Write(Encoding.ASCII.GetBytes(header.ToString()));
                        if (reply.StallAfterHeaders) await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
                        if (reply.BodyDelayMs > 0) await Task.Delay(reply.BodyDelayMs, stop.Token).ConfigureAwait(false);
                        var take = Math.Min(payload.Length, reply.TruncateAfterBytes ?? payload.Length);
                        var chunk = reply.ChunkBytes == 0 ? Math.Max(1, take) : reply.ChunkBytes;
                        for (var offset = 0; offset < take; offset += chunk) {
                            var count = Math.Min(chunk, take - offset);
                            if (reply.Chunked) await Write(Encoding.ASCII.GetBytes($"{count:x}\r\n"));
                            await Write(payload.AsSpan(offset, count).ToArray());
                            if (reply.Chunked) await Write("\r\n"u8.ToArray());
                            if (reply.ChunkDelayMs > 0) await Task.Delay(reply.ChunkDelayMs, stop.Token).ConfigureAwait(false);
                        }
                        if (reply.TruncateAfterBytes is not null) return;
                        if (reply.Chunked) await Write("0\r\n\r\n"u8.ToArray());
                        Journal.Write("Response", new { request.Id, reply.Status, payloadBytes = payload.Length, connection });
                        if (reply.CloseAfter) return;
                    }
                }
            }
        } catch (Exception e) when (e is not InvalidDataException && e is OperationCanceledException or IOException or SocketException or ObjectDisposedException or AuthenticationException) {
            Journal.Write("ConnectionEnded", new { connection, reason = e.GetType().Name, bytesSent });
        } catch (Exception e) { errors.Enqueue(e); Journal.Write("ServerError", new { connection, type = e.GetType().Name }); }
        finally { clients.TryRemove(connection, out _); Journal.Write("ConnectionClosed", new { connection, bytesSent }); }
    }
    private static async Task<string?> ReadLine(StreamReader reader, CancellationToken token) {
        var buffer = new char[1]; var line = new StringBuilder();
        while (line.Length <= 16384) {
            if (await reader.ReadAsync(buffer, token).ConfigureAwait(false) == 0) return line.Length == 0 ? null : throw new InvalidDataException("Incomplete line");
            if (buffer[0] == '\n') return line.ToString().TrimEnd('\r');
            line.Append(buffer[0]);
        }
        throw new InvalidDataException("Line too long");
    }
    private (FaultReply Reply, int Ordinal) Plan(int device, string method, string member, Dictionary<string, string> parameters) {
        var definition = Scenario.Devices.FirstOrDefault(d => d.Number == device);
        if (definition is null) return (FaultReply.Http(404), 0);
        var rule = Scenario.Rules.FirstOrDefault(r => r.Device == device && r.Method == method && r.Member == member);
        if (rule is not null) {
            if (!active) return (rule.Baseline, 0);
            var ordinal = ordinals.GetValueOrDefault(rule) + 1; ordinals[rule] = ordinal;
            var remaining = ordinal;
            foreach (var step in rule.Steps) { if (remaining <= step.Repeat) return (step.Reply, ordinal); remaining -= step.Repeat; }
            return (rule.After, ordinal);
        }
        if (method == "PUT" && member is "connect" or "connected") {
            connected[device] = member == "connect" || parameters.GetValueOrDefault("Connected") == "true";
            return (new() { Value = JsonSerializer.SerializeToElement<object?>(null) }, 0);
        }
        return (new() { Value = JsonSerializer.SerializeToElement<object>(member switch {
            "interfaceversion" => definition.InterfaceVersion, "connected" => connected[device], "connecting" => false, _ => true
        }) }, 0);
    }
    private static string Expand(string body, uint transaction, long serverId) => body.Replace("${clientTransaction}", transaction.ToString(CultureInfo.InvariantCulture)).Replace("${serverTransaction}", serverId.ToString(CultureInfo.InvariantCulture));
    private static byte[] Payload(FaultReply reply, uint transaction, long serverId) {
        if (reply.BodyBase64 is not null) return Convert.FromBase64String(reply.BodyBase64);
        if (reply.Body is not null) return Encoding.UTF8.GetBytes(Expand(reply.Body, transaction, serverId));
        var envelope = new Dictionary<string, object?> { ["ClientTransactionID"] = transaction, ["ServerTransactionID"] = serverId, ["Value"] = reply.Value };
        if (!reply.OmitErrorFields) { envelope["ErrorNumber"] = reply.ErrorNumber; envelope["ErrorMessage"] = reply.ErrorMessage; }
        return JsonSerializer.SerializeToUtf8Bytes(envelope);
    }
    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); lock (gate) { listening = false; listener.Stop(); }
        await acceptLoop.ConfigureAwait(false);
        foreach (var client in clients.Values) client.Dispose();
        await Task.WhenAll(handlers.Values).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        stop.Dispose();
    }
}
