using Microsoft.Extensions.Time.Testing;
using Nina.FieldKit.Core.Safety;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class SafetyAggregateTests {
    private sealed class ControlledClient(TimeProvider clock) : ISafetyEndpointClient {
        public PollOutcome Outcome = PollOutcome.Observation;
        public bool Safe = true;
        public Task<SafetyPollResult> PollAsync(CancellationToken token) {
            var now = (double)clock.GetTimestamp() / clock.TimestampFrequency;
            return Task.FromResult(new SafetyPollResult(Outcome, Outcome == PollOutcome.Observation ? Safe : null,
                "Controlled observation", now, now));
        }
        public void Dispose() { }
    }

    [Fact] public async Task GraceCanPreserveButCannotRestoreAggregateSafety() {
        var clock = new FakeTimeProvider();
        var a = SafetyStateTests.Options with { AttemptsPerCycle = 1, FailedCyclesToUnsafe = 100 };
        var b = SafetyStateTests.Options with { BaseUrl = "http://localhost:22222" };
        var clientA = new ControlledClient(clock);
        var clientB = new ControlledClient(clock);
        using var service = new SafetyMonitorService(new() { Endpoints = [a, b] }, clock, o => o.Id == a.Id ? clientA : clientB);
        service.Start();
        await SafetyServiceTests.Until(() => service.Snapshot().IsSafe && service.Snapshot().Endpoints.All(e => e.NextRequestInSeconds is not null));
        clientA.Outcome = PollOutcome.TransientFailure;
        clock.Advance(TimeSpan.FromSeconds(2));
        await SafetyServiceTests.Until(() => service.Snapshot().Endpoints[0].Phase == EndpointPhase.GraceSafe && service.Snapshot().Endpoints.All(e => e.NextRequestInSeconds > 0));
        Assert.True(service.Snapshot().IsSafe);
        clientB.Safe = false;
        clock.Advance(TimeSpan.FromSeconds(2));
        await SafetyServiceTests.Until(() => service.Snapshot().Endpoints[1].Phase == EndpointPhase.Unsafe && service.Snapshot().Endpoints.All(e => e.NextRequestInSeconds > 0));
        Assert.False(service.Snapshot().IsSafe);
        clientB.Safe = true;
        clock.Advance(TimeSpan.FromSeconds(2));
        await SafetyServiceTests.Until(() => service.Snapshot().Endpoints[1].Phase == EndpointPhase.FreshSafe);
        Assert.False(service.Snapshot().IsSafe); // A's old cached safe value cannot restore the aggregate.
    }

    [Fact] public async Task EveryConfiguredEnabledEndpointIsRequired() {
        var clock = new FakeTimeProvider();
        var first = SafetyStateTests.Options;
        var second = SafetyStateTests.Options with { BaseUrl = "http://localhost:22222" };
        var safe = new ControlledClient(clock);
        var unsafeClient = new ControlledClient(clock) { Safe = false };
        using var service = new SafetyMonitorService(new() { Endpoints = [first, second] }, clock,
            o => o.Id == first.Id ? safe : unsafeClient);
        service.Start();
        await SafetyServiceTests.Until(() => service.Snapshot().Endpoints.All(e => e.RawIsSafe is not null));
        Assert.False(service.Snapshot().IsSafe);
    }
}
