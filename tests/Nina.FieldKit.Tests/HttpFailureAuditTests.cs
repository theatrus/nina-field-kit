using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Nina.FieldKit.Core.Safety;
using Nina.FieldKit.Testing;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class HttpFailureAuditTests {
    [Theory]
    [InlineData(HttpRequestError.ConnectionError, true)]
    [InlineData(HttpRequestError.NameResolutionError, true)]
    [InlineData(HttpRequestError.ResponseEnded, true)]
    [InlineData(HttpRequestError.SecureConnectionError, false)]
    [InlineData(HttpRequestError.InvalidResponse, false)]
    [InlineData(HttpRequestError.ConfigurationLimitExceeded, false)]
    [InlineData(HttpRequestError.ProxyTunnelError, false)]
    [InlineData(HttpRequestError.VersionNegotiationError, false)]
    [InlineData(HttpRequestError.HttpProtocolError, false)]
    [InlineData(HttpRequestError.UserAuthenticationError, false)]
    [InlineData(HttpRequestError.ExtendedConnectNotSupported, false)]
    [InlineData(HttpRequestError.Unknown, false)]
    public async Task EveryNet8HttpRequestErrorHasExplicitFailurePolicy(HttpRequestError error, bool transient) {
        using var handler = new AlpacaClientTests.Handler { Respond = (_, _, _) => throw new HttpRequestException(error, "synthetic-secret") };
        using var client = new AlpacaSafetyClient(SafetyStateTests.Options, handler: handler);
        var result = await client.PollAsync(default);
        Assert.Equal(transient ? PollOutcome.TransientFailure : PollOutcome.PermanentFailure, result.Outcome);
        Assert.DoesNotContain("synthetic-secret", result.Reason);
    }

    [Theory] [InlineData(SocketError.HostNotFound, false)] [InlineData(SocketError.TryAgain, true)]
    public async Task DnsHostNotFoundAndTemporaryFailureAreDistinct(SocketError code, bool transient) {
        using var handler = new AlpacaClientTests.Handler { Respond = (_, _, _) => throw new HttpRequestException(HttpRequestError.NameResolutionError, "private host", new SocketException((int)code)) };
        using var client = new AlpacaSafetyClient(SafetyStateTests.Options, handler: handler);
        var result = await client.PollAsync(default);
        Assert.Equal(transient ? PollOutcome.TransientFailure : PollOutcome.PermanentFailure, result.Outcome);
        Assert.DoesNotContain("private host", result.Reason);
    }

    [Fact] public async Task CallerCancellationIsNotReportedAsMissedCheck() {
        await using var run = new FaultServerTests.Run(new() { Kind = ReplyKind.Stall }, "cancel");
        using var client = new AlpacaSafetyClient(run.Options with { RequestTimeoutSeconds = 2 });
        run.Server.Activate(); using var cancellation = new CancellationTokenSource();
        var task = client.PollAsync(cancellation.Token);
        await AcceptanceSuite.Until(() => run.Server.Requests.Any(r => r.Member == "issafe"), TimeSpan.FromSeconds(2));
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact] public async Task RedirectTargetReceivesNoRequestsAndCookiesAreNotReplayed() {
        await using var target = new FaultServerTests.Run(FaultReply.Safe(), "redirect-target");
        await using var redirect = new FaultServerTests.Run(FaultReply.Http(302) with { Headers = new() { ["Location"] = target.Server.BaseUrl + "api/v1/safetymonitor/0/issafe" } }, "redirect");
        using var client = new AlpacaSafetyClient(redirect.Options); redirect.Server.Activate();
        Assert.Equal(PollOutcome.PermanentFailure, (await client.PollAsync(default)).Outcome);
        Assert.Equal(0, target.Server.RequestCount);
        Assert.False(AlpacaSafetyClient.CreateHandler(redirect.Options).UseCookies);
    }

    [Theory] [InlineData(1031, true)] [InlineData(1024, false)] [InlineData(1025, false)] [InlineData(1280, false)]
    public async Task AlpacaErrorsNeverAuthorizeSafeAndNotConnectedRepeatsPreparation(int code, bool transient) {
        await using var run = new FaultServerTests.Run(new() { ErrorNumber = code, ErrorMessage = "synthetic-secret" }, "alpaca-error");
        using var client = new AlpacaSafetyClient(run.Options); await client.PollAsync(default); run.Server.Activate();
        var result = await client.PollAsync(default);
        Assert.Equal(transient ? PollOutcome.TransientFailure : PollOutcome.PermanentFailure, result.Outcome);
        Assert.Null(result.IsSafe); Assert.Equal(code, result.ErrorNumber); Assert.DoesNotContain("synthetic-secret", result.Reason);
        var baseline = run.Server.RequestCount;
        await client.PollAsync(default);
        Assert.Equal(transient ? 3 : 1, run.Server.RequestCount - baseline);
    }

    [Theory] [InlineData(65535, false)] [InlineData(65536, false)] [InlineData(65537, false)]
    [InlineData(65535, true)] [InlineData(65536, true)] [InlineData(65537, true)]
    public async Task ExactResponseByteLimit(int size, bool chunked) {
        // First issafe is request 3; expansion replaces each transaction placeholder with one digit.
        var template = FaultServerTests.Envelope[..^1] + ",\"padding\":\"\"}";
        var expanded = template.Replace("${clientTransaction}", "3").Replace("${serverTransaction}", "3");
        var body = template[..^2] + new string('x', size - Encoding.UTF8.GetByteCount(expanded)) + "\"}";
        await using var run = new FaultServerTests.Run(new() { Kind = ReplyKind.Raw, Body = body, Chunked = chunked }, "exact-size");
        run.Server.Activate(); using var client = new AlpacaSafetyClient(run.Options with { RequestTimeoutSeconds = 2 });
        var result = await client.PollAsync(default);
        Assert.Equal(size <= 65536 ? PollOutcome.Observation : PollOutcome.PermanentFailure, result.Outcome);
    }

    [Theory] [InlineData(15, true)] [InlineData(16, false)]
    public async Task JsonDepthLimitIncludesRoot(int nested, bool valid) {
        var body = FaultServerTests.Envelope[..^1] + ",\"nested\":" + new string('[', nested) + "0" + new string(']', nested) + "}";
        await using var run = new FaultServerTests.Run(new() { Kind = ReplyKind.Raw, Body = body }, "depth");
        run.Server.Activate(); using var client = new AlpacaSafetyClient(run.Options);
        Assert.Equal(valid ? PollOutcome.Observation : PollOutcome.PermanentFailure, (await client.PollAsync(default)).Outcome);
    }

    [Fact] public async Task InvalidUtf8AndOversizedHeadersCannotAuthorizeSafety() {
        await using var utf8 = new FaultServerTests.Run(new() { Kind = ReplyKind.Raw, BodyBase64 = Convert.ToBase64String([0xff, 0xfe, 0xff]) }, "utf8");
        utf8.Server.Activate(); using var a = new AlpacaSafetyClient(utf8.Options);
        Assert.Equal(PollOutcome.PermanentFailure, (await a.PollAsync(default)).Outcome);
        await using var headers = new FaultServerTests.Run(new() { Headers = new() { ["X-Padding"] = new string('x', 20000) } }, "header-limit");
        headers.Server.Activate(); using var b = new AlpacaSafetyClient(headers.Options);
        var result = await b.PollAsync(default); Assert.Equal(PollOutcome.PermanentFailure, result.Outcome);
        Assert.Contains("ConfigurationLimitExceeded", result.Reason);
    }

    [Fact] public async Task InvalidUtf8InUnusedJsonPropertyIsStillMalformed() {
        var prefix = FaultServerTests.Envelope.Replace("${clientTransaction}", "3").Replace("${serverTransaction}", "3")[..^1] + ",\"unused\":\"";
        var bytes = Encoding.UTF8.GetBytes(prefix).Concat(new byte[] { 0xff }).Concat("\"}"u8.ToArray()).ToArray();
        await using var run = new FaultServerTests.Run(new() { Kind = ReplyKind.Raw, BodyBase64 = Convert.ToBase64String(bytes) }, "utf8-unused");
        run.Server.Activate(); using var client = new AlpacaSafetyClient(run.Options);
        Assert.Equal(PollOutcome.PermanentFailure, (await client.PollAsync(default)).Outcome);
    }

    [Theory] [InlineData("3")] [InlineData("Wed, 01 Jan 2020 00:00:00 GMT")] [InlineData("invalid")]
    public async Task RetryAfterHeaderParsingUsesProductionTransport(string value) {
        await using var run = new FaultServerTests.Run(FaultReply.Http(503) with { Headers = new() { ["Retry-After"] = value } }, "retry-after-parse");
        run.Server.Activate(); using var client = new AlpacaSafetyClient(run.Options);
        var result = await client.PollAsync(default);
        Assert.Equal(PollOutcome.TransientFailure, result.Outcome);
        Assert.Equal(value == "3" ? TimeSpan.FromSeconds(3) : value == "invalid" ? null : TimeSpan.Zero, result.RetryAfter);
    }

    [Fact] public async Task FutureDateRetryAfterAndDrippedBodyTimeout() {
        await using var date = new FaultServerTests.Run(FaultReply.Http(429) with { Headers = new() { ["Retry-After"] = DateTimeOffset.UtcNow.AddSeconds(10).ToString("R") } }, "retry-date");
        date.Server.Activate(); using var a = new AlpacaSafetyClient(date.Options);
        Assert.InRange((await a.PollAsync(default)).RetryAfter!.Value.TotalSeconds, 7, 11);
        await using var drip = new FaultServerTests.Run(new() { ChunkBytes = 1, ChunkDelayMs = 50 }, "drip");
        drip.Server.Activate(); using var b = new AlpacaSafetyClient(drip.Options);
        Assert.Equal(PollOutcome.TransientFailure, (await b.PollAsync(default)).Outcome);
        await using var lowLatency = new FaultServerTests.Run(new() { HeaderDelayMs = 40, BodyDelayMs = 40 }, "low-latency");
        lowLatency.Server.Activate(); using var c = new AlpacaSafetyClient(lowLatency.Options with { RequestTimeoutSeconds = 1 });
        Assert.True((await c.PollAsync(default)).IsSafe);
    }
}
