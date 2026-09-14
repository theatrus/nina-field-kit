using System.Diagnostics;
using System.Text.Json;
using Nina.FieldKit.Core.Safety;

namespace Nina.FieldKit.Testing;

// Real elapsed time, production transport and policy. No fake safety state or fake socket clock.
public static class AcceptanceSuite {
    public static async Task<int> RunAsync(string suite, string output) {
        if (suite is not ("defaults" or "soak")) throw new ArgumentException("Suite must be defaults or soak");
        Directory.CreateDirectory(output);
        using var journal = new RunJournal(Path.Combine(output, "server.jsonl"));
        using var monitor = new RunJournal(Path.Combine(output, "monitor.jsonl"));
        var watch = Stopwatch.StartNew();
        var scenario = new FaultScenario { Id = suite, MaximumRunSeconds = suite == "soak" ? 2300 : 600,
            Rules = [new() { After = FaultReply.Http(503) }] };
        await using var server = new FaultServer(scenario, journal: journal);
        var options = new SafetyEndpointOptions { BaseUrl = server.BaseUrl, PollSeconds = suite == "soak" ? 5 : 30 };
        File.WriteAllText(Path.Combine(output, "scenario.json"), JsonSerializer.Serialize(new { scenario, options, runtime = Environment.Version.ToString(), os = Environment.OSVersion.ToString() }, FaultScenario.Json));
        using var service = new SafetyMonitorService(new() { Endpoints = [options] }, log: monitor.Write);
        service.Changed += () => monitor.Write("Snapshot", service.Snapshot());
        var passed = false; string? failure = null;
        try {
            service.Start();
            await Until(() => service.Snapshot().IsSafe, TimeSpan.FromSeconds(100));
            if (suite == "defaults") {
                var safeAge = service.Snapshot().Endpoints[0].SafeAgeSeconds!.Value;
                var activation = Stopwatch.StartNew(); server.Activate();
                await Until(() => !service.Snapshot().IsSafe, TimeSpan.FromSeconds(95));
                var snap = service.Snapshot();
                if (snap.Endpoints[0].Phase != EndpointPhase.Stale || activation.Elapsed.TotalSeconds + safeAge < 89 || activation.Elapsed.TotalSeconds + safeAge > 92)
                    throw new InvalidOperationException("Default evidence expiry did not occur at the independent 90-second deadline");
                if (snap.Endpoints[0].FailedCycles < 2) throw new InvalidOperationException("Expected two exhausted checks before default age expiry");
                server.Reset();
                await Until(() => service.Snapshot().IsSafe, TimeSpan.FromSeconds(150));
            } else {
                var until = TimeSpan.FromMinutes(35);
                while (watch.Elapsed < until) {
                    if (!service.Snapshot().IsSafe) throw new InvalidOperationException("Healthy soak unexpectedly lost safety");
                    await Task.Delay(500);
                }
                var requests = server.Requests.Where(r => r.Member == "issafe").ToArray();
                if (requests.Select(r => r.Connection).Distinct().Count() < 2) throw new InvalidOperationException("No socket renewal observed");
                foreach (var connection in requests.GroupBy(r => r.Connection))
                    if (connection.Max(r => r.ElapsedSeconds) - connection.Min(r => r.ElapsedSeconds) > 1801) throw new InvalidOperationException("Connection reused beyond 30 minutes");
            }
            if (server.Errors.Length != 0) throw new InvalidOperationException("Fault server failed internally");
            service.Dispose(); await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            if (service.Snapshot().IsSafe) throw new InvalidOperationException("Disconnect retained safety");
            passed = true;
        } catch (Exception e) { failure = e.ToString(); }
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { suite, passed, failure, elapsedSeconds = watch.Elapsed.TotalSeconds }, FaultScenario.Json));
        return passed ? 0 : 2;
    }
    public static async Task Until(Func<bool> condition, TimeSpan timeout) {
        var start = Stopwatch.StartNew();
        while (!condition()) { if (start.Elapsed > timeout) throw new TimeoutException("Condition deadline exceeded"); await Task.Delay(10); }
    }
}
