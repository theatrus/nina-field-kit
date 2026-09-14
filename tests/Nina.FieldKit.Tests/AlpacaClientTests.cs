using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Nina.FieldKit.Core.Safety;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class AlpacaClientTests {
    internal sealed class Handler : HttpMessageHandler {
        public ConcurrentQueue<(string Method, string Path, string Parameters)> Requests { get; } = new();
        public Func<HttpRequestMessage, uint, CancellationToken, Task<HttpResponseMessage>>? Respond;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            var parameters = request.Method == HttpMethod.Get ? request.RequestUri!.Query.TrimStart('?') : await request.Content!.ReadAsStringAsync(token);
            var values = parameters.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
            Assert.True(uint.Parse(values["ClientID"]) > 0);
            var id = uint.Parse(values["ClientTransactionID"]);
            Requests.Enqueue((request.Method.Method, request.RequestUri!.AbsolutePath, parameters));
            Assert.True(request.Headers.CacheControl?.NoCache);
            Assert.True(request.Headers.CacheControl?.NoStore);
            return Respond is null ? Reply(id, request.RequestUri.AbsolutePath.EndsWith("interfaceversion") ? 1 : true) : await Respond(request, id, token);
        }
    }

    internal static HttpResponseMessage Reply(uint transaction, object? value, int error = 0) => new(HttpStatusCode.OK) {
        Content = new StringContent(JsonSerializer.Serialize(new { ClientTransactionID = transaction, ServerTransactionID = 42,
            ErrorNumber = error, ErrorMessage = error == 0 ? "" : "sensitive upstream text", Value = value }), Encoding.UTF8, "application/json")
    };

    [Fact] public async Task ReadOnlyModeValidatesPrefixAndNeverWrites() {
        using var handler = new Handler();
        using var client = new AlpacaSafetyClient(SafetyStateTests.Options with { BaseUrl = "http://localhost/prefix/", DeviceNumber = 2 }, handler: handler);
        Assert.True((await client.PollAsync(default)).IsSafe);
        Assert.True((await client.PollAsync(default)).IsSafe);
        Assert.Equal(4, handler.Requests.Count); // version + connected once, then two polls
        Assert.All(handler.Requests, r => { Assert.Equal("GET", r.Method); Assert.StartsWith("/prefix/api/v1/safetymonitor/2/", r.Path); });
    }

    [Theory] [InlineData(408)] [InlineData(429)] [InlineData(500)] [InlineData(502)] [InlineData(503)] [InlineData(504)]
    public async Task TransientHttpStatusWinsOverHtmlPayload(int status) {
        using var handler = new Handler { Respond = (_, _, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("<html>temporary error</html>") }) };
        using var client = new AlpacaSafetyClient(SafetyStateTests.Options, handler: handler);
        Assert.Equal(PollOutcome.TransientFailure, (await client.PollAsync(default)).Outcome);
        Assert.Single(handler.Requests); // Adapter never retries itself.
    }

    [Theory] [InlineData(301)] [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(405)]
    public async Task PermanentHttpErrorsFailImmediately(int status) {
        using var handler = new Handler { Respond = (_, _, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)) };
        using var client = new AlpacaSafetyClient(SafetyStateTests.Options, handler: handler);
        Assert.Equal(PollOutcome.PermanentFailure, (await client.PollAsync(default)).Outcome);
    }

    [Theory] [InlineData("true")] [InlineData(null)] [InlineData(1)]
    public async Task SafetyValueMustBeBoolean(object? value) {
        using var handler = new Handler { Respond = (r, id, _) => Task.FromResult(Reply(id,
            r.RequestUri!.AbsolutePath.EndsWith("interfaceversion") ? 1 : r.RequestUri.AbsolutePath.EndsWith("connected") ? true : value)) };
        using var client = new AlpacaSafetyClient(SafetyStateTests.Options, handler: handler);
        Assert.Equal(PollOutcome.PermanentFailure, (await client.PollAsync(default)).Outcome);
    }

    [Fact] public async Task NonzeroAlpacaErrorDoesNotAuthorizeSafetyOrLeakServerText() {
        using var handler = new Handler { Respond = (_, id, _) => Task.FromResult(Reply(id, true, 1024)) };
        using var client = new AlpacaSafetyClient(SafetyStateTests.Options, handler: handler);
        var result = await client.PollAsync(default);
        Assert.Equal(PollOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(1024, result.ErrorNumber);
        Assert.Equal(42u, result.ServerTransactionId);
        Assert.DoesNotContain("sensitive", JsonSerializer.Serialize(result));
    }

    [Theory] [InlineData("{")] [InlineData("[]")]
    [InlineData("{\"ClientTransactionID\":1,\"ServerTransactionID\":1,\"ErrorNumber\":0,\"ErrorMessage\":\"\",\"Value\":true,\"Value\":false}")]
    public async Task MalformedOrAmbiguousEnvelopeFailsUnsafe(string payload) {
        using var handler = new Handler { Respond = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) }) };
        using var client = new AlpacaSafetyClient(SafetyStateTests.Options, handler: handler);
        Assert.Equal(PollOutcome.PermanentFailure, (await client.PollAsync(default)).Outcome);
    }

    [Fact] public async Task WrongTransactionIsRejected() {
        using var handler = new Handler { Respond = (_, id, _) => Task.FromResult(Reply(id + 1, 1)) };
        using var client = new AlpacaSafetyClient(SafetyStateTests.Options, handler: handler);
        Assert.Equal(PollOutcome.PermanentFailure, (await client.PollAsync(default)).Outcome);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task StarfrontEnvelopeWithoutErrorFieldsUsesAscomSuccessDefaults(bool safe) {
        // Reduced fixture from the documented Building 4 endpoint, 2026-09-13.
        // Match the official ASCOM client's defaults without defaulting the safety Value.
        using var handler = new Handler { Respond = (r, id, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(JsonSerializer.Serialize(new {
                Value = r.RequestUri!.AbsolutePath.EndsWith("interfaceversion") ? (object)3 :
                    r.RequestUri.AbsolutePath.EndsWith("connected") ? true : safe,
                ClientTransactionID = id, ServerTransactionID = 42
            }))
        }) };
        using var client = new AlpacaSafetyClient(SafetyStateTests.Options, handler: handler);
        var result = await client.PollAsync(default);
        Assert.Equal(PollOutcome.Observation, result.Outcome);
        Assert.Equal(safe, result.IsSafe);
        Assert.Equal(42u, result.ServerTransactionId);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("\"ErrorNumber\":1024", PollOutcome.PermanentFailure)]
    [InlineData("\"ErrorMessage\":\"failure\"", PollOutcome.PermanentFailure)]
    [InlineData("\"ErrorNumber\":0,\"ErrorMessage\":\"failure\"", PollOutcome.PermanentFailure)]
    [InlineData("\"ErrorNumber\":null", PollOutcome.PermanentFailure)]
    [InlineData("\"ErrorMessage\":null", PollOutcome.PermanentFailure)]
    [InlineData("\"ErrorNumber\":\"0\"", PollOutcome.PermanentFailure)]
    [InlineData("\"ErrorMessage\":false", PollOutcome.PermanentFailure)]
    [InlineData("\"ErrorNumber\":0", PollOutcome.Observation)]
    [InlineData("\"ErrorMessage\":\"\"", PollOutcome.Observation)]
    public async Task OmittedErrorFieldsNeverHidePresentErrors(string fields, PollOutcome expected) {
        using var handler = new Handler { Respond = (r, id, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("{\"ClientTransactionID\":" + id + ",\"ServerTransactionID\":42,\"Value\":" +
                (r.RequestUri!.AbsolutePath.EndsWith("interfaceversion") ? "3" : "true") + "," + fields + "}")
        }) };
        using var client = new AlpacaSafetyClient(SafetyStateTests.Options, handler: handler);
        var result = await client.PollAsync(default);
        Assert.Equal(expected, result.Outcome);
        if (expected != PollOutcome.Observation) Assert.Null(result.IsSafe);
    }

    [Theory] [InlineData("null")] [InlineData("\"true\"")] [InlineData("1")] [InlineData("")]
    public async Task OmittedErrorFieldsStillRequireExplicitBooleanSafety(string value) {
        using var handler = new Handler { Respond = (r, id, _) => {
            var memberValue = r.RequestUri!.AbsolutePath.EndsWith("interfaceversion") ? "3" :
                r.RequestUri.AbsolutePath.EndsWith("connected") ? "true" : value;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{\"ClientTransactionID\":" + id + ",\"ServerTransactionID\":42" +
                    (memberValue.Length == 0 ? "" : ",\"Value\":" + memberValue) + "}")
            });
        } };
        using var client = new AlpacaSafetyClient(SafetyStateTests.Options, handler: handler);
        var result = await client.PollAsync(default);
        Assert.Equal(PollOutcome.PermanentFailure, result.Outcome);
        Assert.Null(result.IsSafe);
    }

    [Fact] public async Task OversizeBodyIsRejected() {
        using var handler = new Handler { Respond = (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('a', AlpacaSafetyClient.MaximumResponseBytes + 1)) }) };
        using var client = new AlpacaSafetyClient(SafetyStateTests.Options, handler: handler);
        Assert.Equal(PollOutcome.PermanentFailure, (await client.PollAsync(default)).Outcome);
    }

    [Theory] [InlineData(1)] [InlineData(3)]
    public async Task ManagedConnectionUsesVersionedProtocolAndNoDisconnect(int version) {
        var connected = false;
        using var handler = new Handler { Respond = (r, id, _) => {
            var path = r.RequestUri!.AbsolutePath;
            if (r.Method == HttpMethod.Put) { connected = true; return Task.FromResult(Reply(id, null)); }
            return Task.FromResult(Reply(id, path.EndsWith("interfaceversion") ? version : path.EndsWith("connecting") ? false :
                path.EndsWith("connected") ? connected : true));
        } };
        using (var client = new AlpacaSafetyClient(SafetyStateTests.Options with { ConnectionPolicy = ConnectionPolicy.Managed }, handler: handler))
            Assert.True((await client.PollAsync(default)).IsSafe);
        var put = Assert.Single(handler.Requests, r => r.Method == "PUT");
        Assert.EndsWith(version == 1 ? "/connected" : "/connect", put.Path);
        if (version == 1) Assert.Contains("Connected=true", put.Parameters);
        Assert.DoesNotContain(handler.Requests, r => r.Parameters.Contains("Connected=false") || r.Path.EndsWith("/disconnect"));
    }

    [Fact] public async Task TimeoutPreventsOverlappingRequestsAndDiscardsLateReply() {
        var clock = new FakeTimeProvider();
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler { Respond = (_, _, _) => pending.Task }; // Deliberately ignores cancellation.
        using var client = new AlpacaSafetyClient(SafetyStateTests.Options, clock, handler);
        var poll = client.PollAsync(default);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(PollOutcome.TransientFailure, (await poll.WaitAsync(TimeSpan.FromSeconds(2))).Outcome);
        Assert.Equal(PollOutcome.TransientFailure, (await client.PollAsync(default)).Outcome);
        Assert.Single(handler.Requests);
        pending.SetResult(Reply(1, 1));
    }

    [Fact] public void RetryAfterSupportsDateAndSecondsAndMayExceedCap() {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(TimeSpan.FromHours(1), AlpacaSafetyClient.GetRetryAfter(new RetryConditionHeaderValue(TimeSpan.FromHours(1)), now));
        Assert.Equal(TimeSpan.FromMinutes(5), AlpacaSafetyClient.GetRetryAfter(new RetryConditionHeaderValue(now.AddMinutes(5)), now));
        Assert.Equal(TimeSpan.Zero, AlpacaSafetyClient.GetRetryAfter(new RetryConditionHeaderValue(now.AddSeconds(-1)), now));
    }

    [Fact] public void HttpPoolHasFiniteRenewalNoRedirectsOrCookieCredentials() {
        using var handler = AlpacaSafetyClient.CreateHandler(SafetyStateTests.Options);
        Assert.Equal(TimeSpan.FromMinutes(30), handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromSeconds(30), handler.PooledConnectionIdleTimeout);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }
}
