using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace Nina.FieldKit.Core.Safety;

public interface ISafetyEndpointClient : IDisposable {
    Task<SafetyPollResult> PollAsync(CancellationToken token);
}

// No hidden retry policy: the service owns attempts and delays.
public sealed class AlpacaSafetyClient : ISafetyEndpointClient {
    private readonly SafetyEndpointOptions options;
    private readonly TimeProvider clock;
    private readonly HttpClient http;
    private readonly uint clientId = (uint)Random.Shared.Next(1, int.MaxValue);
    private uint transaction;
    private int? interfaceVersion;
    private bool prepared;
    private Task? unresolvedRequest;
    public const int MaximumResponseBytes = 64 * 1024;
    public static string UserAgent => "NINA-Field-Kit/" + typeof(AlpacaSafetyClient).Assembly.GetName().Version;
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    public static SocketsHttpHandler CreateHandler(SafetyEndpointOptions options) => new() {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        PooledConnectionLifetime = TimeSpan.FromSeconds(options.ConnectionLifetimeSeconds),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(Math.Min(30, options.ConnectionLifetimeSeconds)),
        MaxConnectionsPerServer = 1,
        MaxResponseHeadersLength = 16,
        ConnectTimeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds)
        // Default HTTPS certificate validation is deliberately retained.
    };

    public AlpacaSafetyClient(SafetyEndpointOptions options, TimeProvider? clock = null, HttpMessageHandler? handler = null) {
        options.Validate();
        this.options = options;
        this.clock = clock ?? TimeProvider.System;
        http = new HttpClient(handler ?? CreateHandler(options), true) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    private double Now => (double)clock.GetTimestamp() / clock.TimestampFrequency;

    public async Task<SafetyPollResult> PollAsync(CancellationToken token) {
        var start = Now;
        try {
            token.ThrowIfCancellationRequested();
            if (!prepared) await PrepareAsync(token).ConfigureAwait(false);
            start = Now;
            var reply = await RequestAsync("issafe", null, token).ConfigureAwait(false);
            var safe = BooleanValue(reply.Value);
            return new(PollOutcome.Observation, safe, safe ? "Upstream reports safe" : "Upstream reports unsafe",
                start, Now, ServerTransactionId: reply.Transaction, InterfaceVersion: interfaceVersion, Connected: true);
        } catch (AlpacaFailure error) {
            if (error.ErrorNumber == 0x407) prepared = false;
            return new(error.Transient ? PollOutcome.TransientFailure : PollOutcome.PermanentFailure,
                Reason: error.Message, RequestStarted: start, Received: Now, RetryAfter: error.RetryAfter,
                ErrorNumber: error.ErrorNumber, ServerTransactionId: error.Transaction, InterfaceVersion: interfaceVersion,
                Connected: error.ErrorNumber == 0x407 ? false : null);
        }
    }

    private async Task PrepareAsync(CancellationToken token) {
        var versionReply = await RequestAsync("interfaceversion", null, token).ConfigureAwait(false);
        if (versionReply.Value.ValueKind != JsonValueKind.Number || !versionReply.Value.TryGetInt32(out var version) || version is < 1 or > 3)
            throw new AlpacaFailure(false, "Unsupported SafetyMonitor interface version (supported: 1–3)");
        interfaceVersion = version;
        var connected = BooleanValue((await RequestAsync("connected", null, token).ConfigureAwait(false)).Value);
        if (!connected) {
            if (options.ConnectionPolicy == ConnectionPolicy.ExternallyManaged)
                throw new AlpacaFailure(true, "Upstream is not connected; externally managed mode performs no writes", errorNumber: 0x407);
            if (version >= 3) {
                var connecting = BooleanValue((await RequestAsync("connecting", null, token).ConfigureAwait(false)).Value);
                if (!connecting) await RequestAsync("connect", new Dictionary<string, string>(), token).ConfigureAwait(false);
                if (BooleanValue((await RequestAsync("connecting", null, token).ConfigureAwait(false)).Value))
                    throw new AlpacaFailure(true, "Upstream connection is still in progress", errorNumber: 0x407);
            } else {
                await RequestAsync("connected", new Dictionary<string, string> { ["Connected"] = "true" }, token).ConfigureAwait(false);
            }
            if (!BooleanValue((await RequestAsync("connected", null, token).ConfigureAwait(false)).Value))
                throw new AlpacaFailure(true, "Upstream did not confirm connection", errorNumber: 0x407);
        }
        prepared = true;
    }

    private static bool BooleanValue(JsonElement value) => value.ValueKind switch {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new AlpacaFailure(false, "Alpaca response Value must be a Boolean")
    };

    private async Task<(JsonElement Value, uint Transaction)> RequestAsync(string member,
        Dictionary<string, string>? form, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if (unresolvedRequest is { IsCompleted: false })
            throw new AlpacaFailure(true, "Prior timed-out HTTP operation has not ended; no overlapping request was sent");
        var id = ++transaction;
        if (id == 0) id = ++transaction;
        var route = new Uri(options.NormalizedBaseUri(), $"api/v1/safetymonitor/{options.DeviceNumber}/{member}");
        using var request = new HttpRequestMessage(form is null ? HttpMethod.Get : HttpMethod.Put, route) {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        var identity = new Dictionary<string, string> {
            ["ClientID"] = clientId.ToString(CultureInfo.InvariantCulture),
            ["ClientTransactionID"] = id.ToString(CultureInfo.InvariantCulture)
        };
        if (form is null) {
            request.RequestUri = new Uri(route.AbsoluteUri + $"?ClientID={identity["ClientID"]}&ClientTransactionID={identity["ClientTransactionID"]}");
        } else {
            foreach (var pair in form) identity.Add(pair.Key, pair.Value);
            request.Content = new FormUrlEncodedContent(identity);
        }
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.Pragma.ParseAdd("no-cache");
        request.Headers.Accept.ParseAdd("application/json");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.RequestTimeoutSeconds), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var operation = SendAndReadAsync(request, id, form is not null, linked.Token);
        try {
            var result = await operation.WaitAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return result;
        } catch (OperationCanceledException) {
            unresolvedRequest = operation;
            _ = operation.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            token.ThrowIfCancellationRequested();
            throw new AlpacaFailure(true, "HTTP request timed out");
        } catch (HttpRequestException error) {
            var tls = error.HttpRequestError == HttpRequestError.SecureConnectionError || error.InnerException is AuthenticationException;
            var permanentDns = error.InnerException is SocketException socket && socket.SocketErrorCode == SocketError.HostNotFound;
            var transient = !tls && !permanentDns && error.HttpRequestError is
                (HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.ResponseEnded);
            throw new AlpacaFailure(transient, tls ? "TLS certificate or handshake failed" : "HTTP transport failed: " + error.HttpRequestError);
        } catch (HttpIOException error) {
            // Body reads preserve .NET 8's category too: invalid chunk framing is not a dropped connection.
            throw new AlpacaFailure(error.HttpRequestError is HttpRequestError.ResponseEnded or HttpRequestError.ConnectionError,
                "HTTP response failed: " + error.HttpRequestError);
        } catch (IOException) {
            throw new AlpacaFailure(true, "HTTP response stream ended unexpectedly");
        } catch (JsonException) {
            throw new AlpacaFailure(false, "Malformed Alpaca JSON response");
        } catch (DecoderFallbackException) {
            throw new AlpacaFailure(false, "Alpaca response contains invalid UTF-8");
        } catch (InvalidOperationException) {
            throw new AlpacaFailure(false, "Alpaca response contains an unexpected field type");
        }
    }

    private async Task<(JsonElement Value, uint Transaction)> SendAndReadAsync(HttpRequestMessage request,
        uint expectedTransaction, bool isPut, CancellationToken token) {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var status = (int)response.StatusCode;
        if (status != 200) {
            var retryable = status is 408 or 429 or 500 or 502 or 503 or 504;
            throw new AlpacaFailure(retryable, "HTTP status " + status,
                retryAfter: status is 429 or 503 ? GetRetryAfter(response.Headers.RetryAfter, clock.GetUtcNow()) : null);
        }
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new AlpacaFailure(false, "Alpaca response exceeds 64 KiB limit");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0) {
            if (bytes.Length + read > MaximumResponseBytes) throw new AlpacaFailure(false, "Alpaca response exceeds 64 KiB limit");
            bytes.Write(buffer, 0, read);
        }
        token.ThrowIfCancellationRequested();
        var payload = bytes.ToArray();
        // JsonDocument can defer string decoding for unused fields. Validate the whole wire body,
        // otherwise malformed bytes in an ignored property could accompany an accepted safe value.
        _ = StrictUtf8.GetCharCount(payload);
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new AlpacaFailure(false, "Alpaca envelope must be an object");
        // Duplicate fields are ambiguous and must not authorize safety.
        if (root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != root.EnumerateObject().Count())
            throw new AlpacaFailure(false, "Duplicate Alpaca envelope fields");
        if (!root.TryGetProperty("ClientTransactionID", out var client) || !client.TryGetUInt32(out var clientTransaction) ||
            clientTransaction != expectedTransaction || !root.TryGetProperty("ServerTransactionID", out var server) ||
            !server.TryGetUInt32(out var serverTransaction))
            throw new AlpacaFailure(false, "Invalid Alpaca envelope or mismatched client transaction");
        // Match ASCOM Library ErrorResponse defaults for omitted success fields.
        // Present fields must still have valid types; omission is not JSON null.
        var number = 0;
        var errorMessage = "";
        if (root.TryGetProperty("ErrorNumber", out var error) &&
            (error.ValueKind != JsonValueKind.Number || !error.TryGetInt32(out number)))
            throw new AlpacaFailure(false, "Invalid Alpaca error number type", transaction: serverTransaction);
        if (root.TryGetProperty("ErrorMessage", out var message)) {
            if (message.ValueKind != JsonValueKind.String)
                throw new AlpacaFailure(false, "Invalid Alpaca error message type", transaction: serverTransaction);
            errorMessage = message.GetString()!;
        }
        if (number != 0)
            // Raw server messages can echo URLs/credentials. Keep numeric identity; never log arbitrary response text.
            throw new AlpacaFailure(number == 0x407, "Alpaca error " + number, errorNumber: number, transaction: serverTransaction);
        if (errorMessage.Length != 0)
            throw new AlpacaFailure(false, "Alpaca response reports an error message without a nonzero error number", transaction: serverTransaction);
        if (!root.TryGetProperty("Value", out var value) && !isPut)
            throw new AlpacaFailure(false, "Missing Alpaca Value");
        return (value.ValueKind == JsonValueKind.Undefined ? default : value.Clone(), serverTransaction);
    }

    public static TimeSpan? GetRetryAfter(RetryConditionHeaderValue? value, DateTimeOffset now) {
        if (value?.Delta is TimeSpan delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (value?.Date is DateTimeOffset date) return date <= now ? TimeSpan.Zero : date - now;
        return null;
    }

    public void Dispose() => http.Dispose(); // Never sends Connected=false or Disconnect upstream.

    private sealed class AlpacaFailure(bool transient, string message, TimeSpan? retryAfter = null,
        int? errorNumber = null, uint? transaction = null) : Exception(message) {
        public bool Transient { get; } = transient;
        public TimeSpan? RetryAfter { get; } = retryAfter;
        public int? ErrorNumber { get; } = errorNumber;
        public uint? Transaction { get; } = transaction;
    }
}
