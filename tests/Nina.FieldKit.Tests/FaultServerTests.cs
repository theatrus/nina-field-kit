using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Nina.FieldKit.Core.Safety;
using Nina.FieldKit.Testing;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class FaultServerTests {
    internal const string Envelope = "{\"ClientTransactionID\":${clientTransaction},\"ServerTransactionID\":${serverTransaction},\"Value\":true}";
    internal static string Repository {
        get { var dir = new DirectoryInfo(AppContext.BaseDirectory); while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nina.FieldKit.sln"))) dir = dir.Parent; return dir?.FullName ?? throw new InvalidOperationException("Cannot find test repository"); }
    }
    internal sealed class Run : IAsyncDisposable {
        public FaultServer Server { get; }
        public string Output { get; }
        public RunJournal Monitor { get; }
        private readonly RunJournal serverLog;
        public SafetyEndpointOptions Options { get; }
        public Run(FaultReply after, string name = "fault", FaultStep[]? steps = null, SimulatedDevice[]? devices = null, string member = "issafe", X509Certificate2? certificate = null) {
            Output = Path.Combine(Repository, "artifacts", "fault-tests", name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Output);
            var scenario = new FaultScenario { Id = name, Devices = devices ?? [new()], Rules = [new() { Member = member, After = after, Steps = steps ?? [] }] };
            File.WriteAllText(Path.Combine(Output, "scenario.json"), JsonSerializer.Serialize(scenario, FaultScenario.Json));
            serverLog = new(Path.Combine(Output, "server.jsonl")); Monitor = new(Path.Combine(Output, "monitor.jsonl"));
            Server = new(scenario, basePath: "/proxy/", journal: serverLog, certificate: certificate);
            Options = new() { BaseUrl = Server.BaseUrl, PollSeconds = .1, RequestTimeoutSeconds = .25, InitialBackoffSeconds = .05, BackoffCapSeconds = .2, MaximumSafeAgeSeconds = 10, SafeReadingsToSafe = 1, ReturnToSafeHoldSeconds = 0 };
        }
        public SafetyMonitorService Service(SafetyEndpointOptions? options = null) {
            var service = new SafetyMonitorService(new() { Endpoints = [options ?? Options] }, log: Monitor.Write);
            service.Changed += () => Monitor.Write("Snapshot", service.Snapshot()); return service;
        }
        public async ValueTask DisposeAsync() {
            await Server.DisposeAsync();
            try { Assert.Empty(Server.Errors); }
            finally { Monitor.Dispose(); serverLog.Dispose(); }
        }
    }
    private static Task Until(Func<bool> condition) => AcceptanceSuite.Until(condition, TimeSpan.FromSeconds(8));

    private static async Task UntilSafe(AlpacaSafetyClient client) {
        // A canceled transport can still be unwinding; the client intentionally suppresses overlap.
        var deadline = Stopwatch.StartNew();
        while (true) {
            var result = await client.PollAsync(default);
            if (result.IsSafe == true) return;
            Assert.Equal(PollOutcome.TransientFailure, result.Outcome);
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(2), result.Reason);
            await Task.Delay(50);
        }
    }

    [Theory]
    [InlineData(408, true)] [InlineData(429, true)] [InlineData(500, true)] [InlineData(502, true)] [InlineData(503, true)] [InlineData(504, true)]
    [InlineData(301, false)] [InlineData(302, false)] [InlineData(307, false)] [InlineData(400, false)] [InlineData(401, false)]
    [InlineData(403, false)] [InlineData(404, false)] [InlineData(405, false)] [InlineData(501, false)]
    public async Task RealHttpStatusClassificationAndUserAgent(int status, bool transient) {
        await using var run = new Run(FaultReply.Http(status), "http-" + status);
        using var client = new AlpacaSafetyClient(run.Options);
        Assert.True((await client.PollAsync(default)).IsSafe);
        run.Server.Activate();
        var result = await client.PollAsync(default);
        Assert.Equal(transient ? PollOutcome.TransientFailure : PollOutcome.PermanentFailure, result.Outcome);
        Assert.All(run.Server.Requests, r => Assert.Equal(AlpacaSafetyClient.UserAgent, r.UserAgent));
        Assert.Equal(4, run.Server.RequestCount); // Preparation, baseline, and one fault. Adapter does not retry.
    }

    [Theory]
    [InlineData("<html>not JSON</html>")]
    [InlineData("[]")]
    [InlineData("{\"ClientTransactionID\":${clientTransaction},\"ServerTransactionID\":1}")]
    [InlineData("{\"ClientTransactionID\":${clientTransaction},\"ServerTransactionID\":1,\"Value\":null}")]
    [InlineData("{\"ClientTransactionID\":${clientTransaction},\"ServerTransactionID\":1,\"Value\":\"true\"}")]
    [InlineData("{\"ClientTransactionID\":${clientTransaction},\"ServerTransactionID\":1,\"Value\":1}")]
    [InlineData("{\"ClientTransactionID\":0,\"ServerTransactionID\":1,\"Value\":true}")]
    [InlineData("{\"ClientTransactionID\":\"bad\",\"ServerTransactionID\":1,\"Value\":true}")]
    [InlineData("{\"ClientTransactionID\":${clientTransaction},\"ServerTransactionID\":1,\"Value\":true,\"value\":false}")]
    [InlineData("{\"ClientTransactionID\":${clientTransaction},\"ServerTransactionID\":1,\"Value\":true,\"ErrorNumber\":null}")]
    [InlineData("{\"ClientTransactionID\":${clientTransaction},\"ServerTransactionID\":1,\"Value\":true,\"ErrorMessage\":null}")]
    public async Task CompleteMalformedSuccessWithdrawsWithoutRetry(string body) {
        await using var run = new Run(new() { Kind = ReplyKind.Raw, Body = body }, "malformed");
        using var service = run.Service(); service.Start(); await Until(() => service.Snapshot().IsSafe);
        run.Server.Activate();
        await Until(() => service.Snapshot().Endpoints[0].Phase == EndpointPhase.Faulted);
        Assert.False(service.Snapshot().IsSafe);
        Assert.Equal(0, service.Snapshot().Endpoints[0].FailedCycles);
        Assert.DoesNotContain(run.Monitor.Snapshot(), e => e.Event == "RetryScheduled");
    }

    [Fact] public async Task OmittedSuccessFieldsAcceptedButErrorTextDoesNotLeak() {
        await using var run = new Run(new() { OmitErrorFields = true }, "omitted");
        using var client = new AlpacaSafetyClient(run.Options); run.Server.Activate();
        Assert.True((await client.PollAsync(default)).IsSafe);
        await using var error = new Run(new() { ErrorMessage = "synthetic-secret-marker" }, "redaction");
        using var service = error.Service(); error.Server.Activate(); service.Start();
        await Until(() => service.Snapshot().Endpoints[0].Phase == EndpointPhase.Faulted);
        Assert.DoesNotContain("synthetic-secret-marker", JsonSerializer.Serialize(service.Snapshot()));
        Assert.DoesNotContain("synthetic-secret-marker", JsonSerializer.Serialize(error.Monitor.Snapshot()));
    }

    [Fact] public async Task RetriesRecoverWithinOneCheckAndAreRecordedWithoutTrace() {
        await using var run = new Run(FaultReply.Safe(), "retry-recovery", [new(2, FaultReply.Http(503))]);
        using var service = run.Service(); service.Start(); await Until(() => service.Snapshot().IsSafe);
        var baseline = service.Snapshot().Endpoints[0].FailedPolls;
        var states = new ConcurrentQueue<bool>(); service.Changed += () => states.Enqueue(service.Snapshot().IsSafe);
        run.Server.Activate(); await Until(() => run.Monitor.Snapshot().Any(e => e.Event == "CheckRecovered"));
        Assert.DoesNotContain(false, states);
        Assert.Equal(baseline + 2, service.Snapshot().Endpoints[0].FailedPolls);
        Assert.Equal(0, service.Snapshot().Endpoints[0].FailedCycles);
        Assert.Equal(2, run.Monitor.Snapshot().Count(e => e.Event == "RetryScheduled"));
        Assert.Equal(3, JsonSerializer.SerializeToElement(run.Monitor.Snapshot().Single(e => e.Event == "CheckRecovered").Data).GetProperty("attempt").GetInt32());
    }

    [Fact] public async Task NineFailedAttemptsBecomeThreeMissedChecks() {
        await using var run = new Run(FaultReply.Http(502), "missed-checks");
        using var service = run.Service(); service.Start(); await Until(() => service.Snapshot().IsSafe);
        run.Server.Activate();
        await Until(() => service.Snapshot().Endpoints[0].FailedCycles >= 2);
        Assert.True(service.Snapshot().IsSafe);
        await Until(() => !service.Snapshot().IsSafe);
        var source = service.Snapshot().Endpoints[0];
        Assert.Equal(3, source.FailedCycles); Assert.Equal(9, source.FailedPolls); Assert.Equal(EndpointPhase.Unsafe, source.Phase);
        service.Dispose(); await service.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task LatencyBeforeHeadersOrDuringBodyTimesOutAndCannotRefreshEvidence(bool body) {
        await using var run = new Run(new() { HeaderDelayMs = body ? 0 : 1500, BodyDelayMs = body ? 1500 : 0 }, "latency");
        using var service = run.Service(run.Options with { MaximumSafeAgeSeconds = .8, FailedCyclesToUnsafe = 100 });
        service.Start(); await Until(() => service.Snapshot().IsSafe); run.Server.Activate();
        await Until(() => service.Snapshot().Endpoints[0].FailedPolls >= 1);
        await Until(() => !service.Snapshot().IsSafe);
        Assert.Equal(EndpointPhase.Stale, service.Snapshot().Endpoints[0].Phase);
        await Task.Delay(1600); Assert.False(service.Snapshot().IsSafe);
        Assert.DoesNotContain(run.Monitor.Snapshot(), e => e.Event == "CheckRecovered");
    }

    [Theory] [InlineData(ReplyKind.Close)] [InlineData(ReplyKind.Reset)]
    public async Task ConnectionBreaksAreRetryableAndListenerCanRecover(ReplyKind kind) {
        await using var run = new Run(new() { Kind = kind }, "connection");
        using var client = new AlpacaSafetyClient(run.Options); await client.PollAsync(default); run.Server.Activate();
        Assert.Equal(PollOutcome.TransientFailure, (await client.PollAsync(default)).Outcome);
        run.Server.Reset(); await UntilSafe(client);
        await run.Server.PauseAsync(); Assert.Equal(PollOutcome.TransientFailure, (await client.PollAsync(default)).Outcome);
        run.Server.Resume(); await UntilSafe(client);
    }

    [Fact] public async Task TruncatedBodyIsTransientButInvalidChunkFramingIsPermanent() {
        await using var truncated = new Run(new() { TruncateAfterBytes = 10 }, "truncated");
        using var a = new AlpacaSafetyClient(truncated.Options); truncated.Server.Activate();
        Assert.Equal(PollOutcome.TransientFailure, (await a.PollAsync(default)).Outcome);
        await using var malformed = new Run(new() { Kind = ReplyKind.Raw,
            RawHttp = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nnot-hex\r\n123\r\n0\r\n\r\n" }, "chunk-framing");
        using var b = new AlpacaSafetyClient(malformed.Options); malformed.Server.Activate();
        var result = await b.PollAsync(default);
        Assert.Equal(PollOutcome.PermanentFailure, result.Outcome);
        Assert.Contains("InvalidResponse", result.Reason);
    }

    [Fact] public async Task RetryAfterDoesNotBlockExpiryOrDisconnect() {
        await using var run = new Run(FaultReply.Http(429) with { Headers = new() { ["Retry-After"] = "3600" } }, "retry-after");
        using var service = run.Service(run.Options with { MaximumSafeAgeSeconds = .8 }); service.Start(); await Until(() => service.Snapshot().IsSafe);
        run.Server.Activate(); await Until(() => service.Snapshot().Endpoints[0].NextRequestInSeconds > 3500);
        var count = run.Server.RequestCount;
        await Until(() => !service.Snapshot().IsSafe); Assert.Equal(count, run.Server.RequestCount);
        Assert.Equal(0, service.Snapshot().Endpoints[0].FailedCycles);
        service.Dispose(); await service.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100); Assert.Equal(count, run.Server.RequestCount);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task BodySizeLimitAppliesToDeclaredAndStreamedBodies(bool chunked) {
        var body = Envelope[..^1] + ",\"padding\":\"" + new string('x', 65536) + "\"}";
        await using var run = new Run(new() { Kind = ReplyKind.Raw, Body = body, Chunked = chunked, ChunkBytes = 4096 }, "body-limit");
        using var client = new AlpacaSafetyClient(run.Options); run.Server.Activate();
        var result = await client.PollAsync(default); Assert.Equal(PollOutcome.PermanentFailure, result.Outcome); Assert.Contains("64 KiB", result.Reason);
    }

    [Fact] public async Task InvalidCertificateFailsWithoutTrustBypass() {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=invalid.example", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        await using var run = new Run(FaultReply.Safe(), "tls", certificate: certificate);
        using var client = new AlpacaSafetyClient(run.Options with { RequestTimeoutSeconds = 2 });
        var result = await client.PollAsync(default);
        Assert.Equal(PollOutcome.PermanentFailure, result.Outcome); Assert.Contains("TLS", result.Reason);
    }

    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task ManagedPreparationUsesVersionedCommandsAndUserAgent(int version) {
        await using var run = new Run(FaultReply.Safe(), "connect", devices: [new(0, version, false)]);
        using var readOnly = new AlpacaSafetyClient(run.Options);
        Assert.Equal(PollOutcome.TransientFailure, (await readOnly.PollAsync(default)).Outcome);
        Assert.DoesNotContain(run.Server.Requests, r => r.Method == "PUT");
        using (var managed = new AlpacaSafetyClient(run.Options with { ConnectionPolicy = ConnectionPolicy.Managed }))
            Assert.True((await managed.PollAsync(default)).IsSafe);
        Assert.Equal(version == 3 ? "connect" : "connected", Assert.Single(run.Server.Requests, r => r.Method == "PUT").Member);
        Assert.All(run.Server.Requests, r => Assert.Equal(AlpacaSafetyClient.UserAgent, r.UserAgent));
    }

    [Fact] public async Task StalledSourceDoesNotBlockOtherSourcesUnsafeResult() {
        await using var slow = new Run(new() { Kind = ReplyKind.Stall }, "multi-slow");
        await using var unsafeSource = new Run(FaultReply.Safe(false), "multi-unsafe");
        using var service = new SafetyMonitorService(new() { Endpoints = [slow.Options with { RequestTimeoutSeconds = 2 }, unsafeSource.Options] });
        service.Start(); await Until(() => service.Snapshot().IsSafe);
        slow.Server.Activate(); unsafeSource.Server.Activate();
        await AcceptanceSuite.Until(() => !service.Snapshot().IsSafe, TimeSpan.FromSeconds(1));
        Assert.Equal(EndpointPhase.Unsafe, service.Snapshot().Endpoints.Single(s => s.Id == unsafeSource.Options.Id).Phase);
    }

    [Fact] public async Task RealSocketLiveReplacementRejectsRetiredLateSafeReply() {
        await using var run = new Run(new() { BodyDelayMs = 1500 }, "live-edit");
        var config = new SafetyConfiguration { Endpoints = [run.Options with { RequestTimeoutSeconds = 2, MaximumSafeAgeSeconds = 3 }] };
        using var original = new SafetyMonitorService(config); original.Start(); await Until(() => original.Snapshot().IsSafe);
        run.Server.Activate(); await Until(() => run.Server.Requests.Any(r => r.Revision == 1));
        using var next = original.CreateReplacement(config with { Revision = Guid.NewGuid(), Endpoints = [config.Endpoints[0] with { RequestTimeoutSeconds = .25 }] });
        original.Dispose(); next.Start(); Assert.True(next.Snapshot().IsSafe);
        await Until(() => !next.Snapshot().IsSafe);
        Assert.True(next.Snapshot().Endpoints[0].SafeAgeSeconds is null);
        await Task.Delay(1600); Assert.False(next.Snapshot().IsSafe);
    }

    [Fact] public async Task StandaloneProcessControlDoesNotConsumeDataRequests() {
        var output = Path.Combine(Repository, "artifacts", "fault-tests", "process-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        var scenarioPath = Path.Combine(output, "scenario.json"); File.WriteAllText(scenarioPath, JsonSerializer.Serialize(new FaultScenario { Rules = [new() { After = FaultReply.Http(503) }] }, FaultScenario.Json));
        var start = new ProcessStartInfo("dotnet") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { Path.Combine(Repository, "tools/Nina.FieldKit.FaultServer/bin/Release/net8.0/Nina.FieldKit.FaultServer.dll"), "--scenario", scenarioPath, "--output", output }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        try {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(line); using var ready = JsonDocument.Parse(line!);
            using var control = new HttpClient(); var url = ready.RootElement.GetProperty("controlUrl").GetString();
            Assert.Equal(HttpStatusCode.Forbidden, (await control.PostAsync(url + "/activate", null)).StatusCode);
            control.DefaultRequestHeaders.Add("X-Control-Token", ready.RootElement.GetProperty("controlToken").GetString());
            control.DefaultRequestHeaders.Add("Origin", "https://unrelated.example");
            Assert.Equal(HttpStatusCode.Forbidden, (await control.PostAsync(url + "/activate", null)).StatusCode);
            control.DefaultRequestHeaders.Remove("Origin");
            Assert.Equal(HttpStatusCode.OK, (await control.PostAsync(url + "/activate", null)).StatusCode);
            using var status = JsonDocument.Parse(await control.GetStringAsync(url + "/status")); Assert.Equal(0, status.RootElement.GetProperty("requests").GetInt64());
            using var client = new AlpacaSafetyClient(new() { BaseUrl = ready.RootElement.GetProperty("baseUrl").GetString()! });
            Assert.Equal(PollOutcome.TransientFailure, (await client.PollAsync(default)).Outcome);
            await control.PostAsync(url + "/stop", null); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(process.ExitCode == 0, await stderr);
        } finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }

    private sealed class CountingClient(ISafetyEndpointClient inner, Action<int> change) : ISafetyEndpointClient {
        public async Task<SafetyPollResult> PollAsync(CancellationToken token) {
            change(1); try { return await inner.PollAsync(token); } finally { change(-1); }
        }
        public void Dispose() => inner.Dispose();
    }

    [Fact] public async Task SixteenSourcesRespectFourClientSlotsAndDisabledSourceIsNotPolled() {
        var scenario = new FaultScenario { Devices = Enumerable.Range(0, 16).Select(i => new SimulatedDevice(i)).ToArray(),
            Rules = Enumerable.Range(0, 16).Select(i => new FaultRule { Device = i, Baseline = new() { BodyDelayMs = 80 } }).ToArray() };
        await using var server = new FaultServer(scenario);
        var endpoints = scenario.Devices.Select(d => new SafetyEndpointOptions { DeviceNumber = d.Number, BaseUrl = server.BaseUrl,
            PollSeconds = .2, RequestTimeoutSeconds = 1, MaximumSafeAgeSeconds = 10, SafeReadingsToSafe = 1, ReturnToSafeHoldSeconds = 0,
            Enabled = d.Number != 15 }).ToArray();
        var gate = new object(); int current = 0, maximum = 0;
        void Track(int delta) { lock (gate) { current += delta; maximum = Math.Max(maximum, current); } }
        using var service = new SafetyMonitorService(new() { Endpoints = endpoints }, clientFactory: options => new CountingClient(new AlpacaSafetyClient(options), Track));
        service.Start(); await Until(() => service.Snapshot().IsSafe);
        Assert.Equal(15, service.Snapshot().Endpoints.Count); Assert.Equal(4, maximum);
        Assert.DoesNotContain(server.Requests, r => r.Device == 15);
        service.Dispose(); await service.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, current); Assert.Empty(server.Errors);
    }

    [Fact] public async Task NewFaultServerRecordsReuseAndRenewalWithoutAlpacaDisconnect() {
        await using var run = new Run(FaultReply.Safe(), "pool-renewal");
        using var client = new AlpacaSafetyClient(run.Options with { ConnectionLifetimeSeconds = 1 });
        Assert.True((await client.PollAsync(default)).IsSafe); Assert.True((await client.PollAsync(default)).IsSafe);
        Assert.Equal(1, run.Server.Connections);
        await Task.Delay(1150); Assert.True((await client.PollAsync(default)).IsSafe);
        Assert.Equal(2, run.Server.Connections); Assert.DoesNotContain(run.Server.Requests, r => r.Method == "PUT");
    }

    [Fact] public void InvalidScenarioDefinitionsFailBeforeOpeningListener() {
        Assert.Throws<ArgumentException>(() => new FaultServer(new() { Rules = [new(), new()] }));
        Assert.Throws<ArgumentException>(() => new FaultServer(new() { Rules = [new() { Steps = [new(0, FaultReply.Safe())] }] }));
        Assert.Throws<ArgumentException>(() => new FaultServer(new() { Rules = [new() { After = new() { HeaderDelayMs = int.MaxValue } }] }));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<FaultScenario>("{\"unknownField\":true}", FaultScenario.Json));
    }
}
