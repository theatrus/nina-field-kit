using Microsoft.Extensions.Time.Testing;
using Nina.FieldKit.Core.Safety;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class SafetyServiceTests {
    [Fact] public async Task RepeatedLiveEditsRetainSafeOnlyUntilOriginalEvidenceExpires() {
        var clock = new FakeTimeProvider();
        var config = new SafetyConfiguration { Endpoints = [SafetyStateTests.Options] };
        var instances = 0;
        ISafetyEndpointClient Factory(SafetyEndpointOptions _) => Interlocked.Increment(ref instances) == 1
            ? new Client((_, _) => Task.FromResult(SafetyStateTests.Safe(Now(clock))))
            : new Client(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return SafetyStateTests.Safe(Now(clock)); });
        using var original = new SafetyMonitorService(config, clock, Factory);
        original.Start();
        await Until(() => original.Snapshot().IsSafe);
        using var replacement = original.CreateReplacement(config with { Revision = Guid.NewGuid() });
        original.Dispose(); replacement.Start();
        Assert.True(replacement.Snapshot().IsSafe);
        Assert.Contains("retaining previous", replacement.Snapshot().Summary);
        clock.Advance(TimeSpan.FromSeconds(20));
        using var second = replacement.CreateReplacement(config with { Revision = Guid.NewGuid() });
        replacement.Dispose(); second.Start();
        Assert.True(second.Snapshot().IsSafe);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(second.Snapshot().IsSafe);
        Assert.True(second.Snapshot().Connected);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task LiveEditHandsOverToFreshResultsWithoutForcingAnIntermediateState(bool before, bool after) {
        var clock = new FakeTimeProvider();
        var next = new TaskCompletionSource<SafetyPollResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var instances = 0;
        ISafetyEndpointClient Factory(SafetyEndpointOptions _) => Interlocked.Increment(ref instances) == 1
            ? new Client((_, _) => Task.FromResult(before ? SafetyStateTests.Safe(Now(clock)) : SafetyStateTests.Unsafe(Now(clock))))
            : new Client((_, token) => next.Task.WaitAsync(token));
        var config = new SafetyConfiguration { Endpoints = [SafetyStateTests.Options] };
        using var original = new SafetyMonitorService(config, clock, Factory);
        original.Start();
        await Until(() => original.Snapshot().Endpoints[0].CompletedPolls > 0);
        using var replacement = original.CreateReplacement(config with { Revision = Guid.NewGuid() });
        original.Dispose(); replacement.Start();
        Assert.Equal(before, replacement.Snapshot().IsSafe);
        next.SetResult(after ? SafetyStateTests.Safe(Now(clock)) : SafetyStateTests.Unsafe(Now(clock)));
        await Until(() => replacement.Snapshot().Endpoints[0].CompletedPolls > 0);
        Assert.Equal(after, replacement.Snapshot().IsSafe);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UiRefreshesDoNotTriggerPollsAndNormalCyclesRespectInterval(bool safe) {
        var clock = new FakeTimeProvider();
        var client = new Client((_, _) => Task.FromResult(safe ? SafetyStateTests.Safe(Now(clock)) : SafetyStateTests.Unsafe(Now(clock))));
        using var service = new SafetyMonitorService(new() { Endpoints = [SafetyStateTests.Options with { PollSeconds = 2 }] }, clock, _ => client);
        service.Start();
        await Until(() => service.Snapshot().Endpoints[0].NextRequestInSeconds is not null);
        for (var i = 0; i < 1000; i++) service.Snapshot();
        clock.Advance(TimeSpan.FromSeconds(1.9));
        for (var i = 0; i < 1000; i++) service.Snapshot();
        Assert.Equal(1, client.Calls);
        clock.Advance(TimeSpan.FromSeconds(.1));
        await Until(() => client.Calls == 2);
        Assert.Equal(2, client.Calls);
    }
    [Fact] public async Task DiagnosticsTrackAttemptsRecoveryAndAggregateTransitions() {
        var clock = new FakeTimeProvider();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var client = new Client((call, _) => Task.FromResult(call == 1 ? SafetyStateTests.Failure : SafetyStateTests.Safe(Now(clock))));
        using var service = new SafetyMonitorService(new() { Endpoints = [SafetyStateTests.Options] }, clock, _ => client,
            (name, _) => events.Enqueue(name));
        service.Start();
        await Until(() => service.Snapshot().Endpoints[0].NextRequestInSeconds is not null);
        var failed = service.Snapshot().Endpoints[0];
        Assert.Equal(1, failed.CompletedPolls);
        Assert.Equal(1, failed.FailedPolls);
        Assert.Equal(PollOutcome.TransientFailure, failed.LastPoll!.Outcome);
        Assert.Contains("RetryScheduled", events);
        Assert.True(failed.CheckInProgress);
        Assert.Equal(0, failed.FailedCycles);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Until(() => service.Snapshot().IsSafe && events.Count(e => e == "AggregateTransition") == 2);
        await Until(() => events.Contains("CheckRecovered"));
        var recovered = service.Snapshot().Endpoints[0];
        Assert.False(recovered.CheckInProgress);
        Assert.Equal(2, recovered.CompletedPolls);
        Assert.Equal(1, recovered.FailedPolls);
        Assert.Equal(PollOutcome.Observation, recovered.LastPoll!.Outcome);
        Assert.Equal(2, events.Count(e => e == "PollCompleted"));
        for (var i = 0; i < 100; i++) service.Snapshot();
        Assert.Equal(2, events.Count(e => e == "AggregateTransition"));
    }

    [Fact] public async Task BrokenLoggingSinkDoesNotBlockSafety() {
        var clock = new FakeTimeProvider();
        var client = new Client((_, _) => Task.FromResult(SafetyStateTests.Safe(Now(clock))));
        using var service = new SafetyMonitorService(new() { Endpoints = [SafetyStateTests.Options] }, clock, _ => client,
            (_, _) => throw new InvalidOperationException("Logging failed"));
        service.Start();
        await Until(() => service.Snapshot().IsSafe);
        Assert.True(service.Snapshot().IsSafe);
    }
    private sealed class Client(Func<int, CancellationToken, Task<SafetyPollResult>> respond) : ISafetyEndpointClient {
        public int Calls;
        public Task<SafetyPollResult> PollAsync(CancellationToken token) => respond(Interlocked.Increment(ref Calls), token);
        public void Dispose() { }
    }

    internal static async Task Until(Func<bool> ready) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(5, timeout.Token);
    }

    private static double Now(TimeProvider clock) => (double)clock.GetTimestamp() / clock.TimestampFrequency;

    [Fact] public async Task GetterUsesCacheWithoutHttpAndExpiresWhileWorkerHangs() {
        var clock = new FakeTimeProvider();
        var hanging = new TaskCompletionSource<SafetyPollResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client((call, _) => call == 1 ? Task.FromResult(SafetyStateTests.Safe(Now(clock))) : hanging.Task);
        using var service = new SafetyMonitorService(new() { Endpoints = [SafetyStateTests.Options] }, clock, _ => client);
        service.Start();
        await Until(() => service.Snapshot().IsSafe && service.Snapshot().Endpoints[0].NextRequestInSeconds is not null);
        for (var i = 0; i < 1000; i++) Assert.True(service.Snapshot().IsSafe);
        Assert.Equal(1, client.Calls);
        clock.Advance(TimeSpan.FromSeconds(2));
        await Until(() => client.Calls == 2);
        clock.Advance(TimeSpan.FromSeconds(28));
        Assert.False(service.Snapshot().IsSafe);
        Assert.Equal(EndpointPhase.Stale, service.Snapshot().Endpoints[0].Phase);
        service.Dispose();
        hanging.SetResult(SafetyStateTests.Safe(Now(clock)));
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(service.Snapshot().IsSafe);
        Assert.False(service.Snapshot().Connected);
    }

    [Fact] public async Task SlowEndpointDoesNotBlockAnotherSourcesUnsafeReading() {
        var clock = new FakeTimeProvider();
        var a = SafetyStateTests.Options;
        var b = SafetyStateTests.Options with { BaseUrl = "http://localhost:22222" };
        var hanging = new TaskCompletionSource<SafetyPollResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new Client((call, _) => call == 1 ? Task.FromResult(SafetyStateTests.Safe(Now(clock))) : hanging.Task);
        var fast = new Client((call, _) => Task.FromResult(call == 1 ? SafetyStateTests.Safe(Now(clock)) : SafetyStateTests.Unsafe(Now(clock))));
        using var service = new SafetyMonitorService(new() { Endpoints = [a, b] }, clock, o => o.Id == a.Id ? slow : fast);
        service.Start();
        await Until(() => service.Snapshot().IsSafe && service.Snapshot().Endpoints.All(e => e.NextRequestInSeconds is not null));
        clock.Advance(TimeSpan.FromSeconds(2));
        await Until(() => fast.Calls >= 2 && !service.Snapshot().IsSafe);
        Assert.Equal(EndpointPhase.Unsafe, service.Snapshot().Endpoints.Single(e => e.Id == b.Id).Phase);
        service.Dispose();
        hanging.TrySetResult(SafetyStateTests.Safe(Now(clock)));
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact] public async Task ExplicitUnsafeEndsCycleWithoutRetry() {
        var clock = new FakeTimeProvider();
        var client = new Client((_, _) => Task.FromResult(SafetyStateTests.Unsafe(Now(clock))));
        using var service = new SafetyMonitorService(new() { Endpoints = [SafetyStateTests.Options] }, clock, _ => client);
        service.Start();
        await Until(() => service.Snapshot().Endpoints[0].NextRequestInSeconds is not null);
        Assert.Equal(1, client.Calls);
        Assert.Equal(0, service.Snapshot().Endpoints[0].FailedCycles);
        Assert.Equal(1, service.Snapshot().Endpoints[0].UnsafeReadings);
        Assert.False(service.Snapshot().IsSafe);
    }

    [Fact] public async Task RetryAfterWaitDoesNotDelayCacheExpiry() {
        var clock = new FakeTimeProvider();
        var client = new Client((call, _) => Task.FromResult(call == 1 ? SafetyStateTests.Safe(Now(clock)) :
            SafetyStateTests.Failure with { RetryAfter = TimeSpan.FromHours(1) }));
        using var service = new SafetyMonitorService(new() { Endpoints = [SafetyStateTests.Options] }, clock, _ => client);
        service.Start();
        await Until(() => service.Snapshot().IsSafe && service.Snapshot().Endpoints[0].NextRequestInSeconds is not null);
        clock.Advance(TimeSpan.FromSeconds(2));
        await Until(() => service.Snapshot().Endpoints[0].NextRequestInSeconds > 3000);
        Assert.True(service.Snapshot().IsSafe);
        clock.Advance(TimeSpan.FromSeconds(28));
        Assert.False(service.Snapshot().IsSafe);
        Assert.Equal(2, client.Calls);
    }

    [Fact] public async Task ExhaustedAttemptsCountAsOneCycleAndBackoffCarriesOn() {
        var clock = new FakeTimeProvider();
        var client = new Client((_, _) => Task.FromResult(SafetyStateTests.Failure));
        using var service = new SafetyMonitorService(new() { Endpoints = [SafetyStateTests.Options] }, clock, _ => client);
        service.Start();
        await Until(() => service.Snapshot().Endpoints[0].NextRequestInSeconds is not null);
        Assert.Equal(0, service.Snapshot().Endpoints[0].FailedCycles);
        clock.Advance(TimeSpan.FromSeconds(.5));
        await Until(() => client.Calls == 2 && service.Snapshot().Endpoints[0].NextRequestInSeconds > 0);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Until(() => client.Calls == 3 && service.Snapshot().Endpoints[0].FailedCycles == 1);
        Assert.Equal(1, service.Snapshot().Endpoints[0].FailedCycles);
        Assert.False(service.Snapshot().Endpoints[0].CheckInProgress);
        await Until(() => service.Snapshot().Endpoints[0].NextRequestInSeconds > 0);
        clock.Advance(TimeSpan.FromSeconds(2));
        await Until(() => client.Calls == 4 && service.Snapshot().Endpoints[0].NextRequestInSeconds > 0);
        Assert.InRange(service.Snapshot().Endpoints[0].NextRequestInSeconds!.Value, 2, 4);
    }

    [Fact] public async Task PermanentFaultUsesLowRateProbes() {
        var clock = new FakeTimeProvider();
        var client = new Client((_, _) => Task.FromResult(new SafetyPollResult(PollOutcome.PermanentFailure, Reason: "HTTP status 401")));
        using var service = new SafetyMonitorService(new() { Endpoints = [SafetyStateTests.Options] }, clock, _ => client);
        service.Start();
        await Until(() => service.Snapshot().Endpoints[0].NextRequestInSeconds is not null);
        Assert.False(service.Snapshot().IsSafe);
        Assert.Equal(30d, service.Snapshot().Endpoints[0].NextRequestInSeconds);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(1, client.Calls);
    }

    [Fact] public async Task NewServiceGenerationCannotInheritSafeCache() {
        using var service = new SafetyMonitorService(new() { Endpoints = [SafetyStateTests.Options] },
            clientFactory: _ => new Client((_, _) => Task.FromResult(SafetyStateTests.Safe(Now(TimeProvider.System)))));
        service.Start();
        await Until(() => service.Snapshot().IsSafe);
        var oldGeneration = service.Snapshot().Generation;
        service.Dispose();
        using var next = new SafetyMonitorService(new() { Endpoints = [SafetyStateTests.Options] },
            clientFactory: _ => new Client((_, _) => Task.FromResult(SafetyStateTests.Failure)));
        next.Start();
        Assert.False(next.Snapshot().IsSafe);
        Assert.NotEqual(oldGeneration, next.Snapshot().Generation);
    }
}
