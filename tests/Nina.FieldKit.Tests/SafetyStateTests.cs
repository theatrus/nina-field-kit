using Nina.FieldKit.Core.Safety;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class SafetyStateTests {
    [Fact] public void DefaultPolicyToleratesTwoMissedThirtySecondChecksButNotTheThird() {
        var state = new EndpointSafetyState(new SafetyEndpointOptions());
        state.CompleteCycle(Safe(0), 0);
        state.CompleteCycle(Safe(30), 30);
        state.CompleteCycle(Safe(60), 60);
        Assert.True(state.Snapshot(60).PermitsSafe);
        state.CompleteCycle(Failure, 90);
        Assert.True(state.Snapshot(90).PermitsSafe);
        state.CompleteCycle(Failure, 120);
        Assert.True(state.Snapshot(149.9).PermitsSafe);
        state.CompleteCycle(Failure, 150);
        Assert.False(state.Snapshot(150).PermitsSafe);
    }
    internal static SafetyEndpointOptions Options => new() { PollSeconds = 2, MaximumSafeAgeSeconds = 30, SafeReadingsToSafe = 1, ReturnToSafeHoldSeconds = 0 };
    internal static SafetyPollResult Safe(double at) => new(PollOutcome.Observation, true, RequestStarted: at, Received: at);
    internal static SafetyPollResult Unsafe(double at) => Safe(at) with { IsSafe = false };
    internal static SafetyPollResult Failure => new(PollOutcome.TransientFailure, Reason: "HTTP status 502");

    [Fact] public void DefaultChecksEveryThirtySecondsAndToleratesTwoMissedChecks() {
        var options = new SafetyEndpointOptions();
        Assert.Equal(30, options.PollSeconds);
        Assert.Equal(90, options.MaximumSafeAgeSeconds);
        Assert.Equal(3, options.FailedCyclesToUnsafe);
        options.Validate();
        Assert.Equal(1800, options.ConnectionLifetimeSeconds);
        Assert.Throws<ArgumentException>(() => (options with { ConnectionLifetimeSeconds = 1801 }).Validate());
    }

    [Fact] public void StartupAndTransportFailuresNeverSeedCache() {
        var state = new EndpointSafetyState(Options);
        Assert.False(state.Snapshot(0).PermitsSafe);
        state.CompleteCycle(Failure, 1);
        Assert.False(state.Snapshot(1).PermitsSafe);
    }

    [Fact] public void CacheExpiresExactlyAtConfiguredAgeDespitePendingFailures() {
        var state = new EndpointSafetyState(Options with { FailedCyclesToUnsafe = 100 });
        state.CompleteCycle(Safe(0), 0);
        state.CompleteCycle(Failure, 2);
        Assert.Equal(EndpointPhase.GraceSafe, state.Snapshot(29.999).Phase);
        Assert.True(state.Snapshot(29.999).PermitsSafe);
        Assert.False(state.Snapshot(30).PermitsSafe);
        Assert.Equal(EndpointPhase.Stale, state.Snapshot(30).Phase);
    }

    [Fact] public void FailedCyclesAndUnsafeReadingsHaveIndependentThresholds() {
        var state = new EndpointSafetyState(Options with { UnsafeReadingsToUnsafe = 3 });
        state.CompleteCycle(Safe(0), 0);
        state.CompleteCycle(Unsafe(2), 2);
        state.CompleteCycle(Failure, 3);
        state.CompleteCycle(Unsafe(4), 4);
        var pending = state.Snapshot(4);
        Assert.True(pending.PermitsSafe);
        Assert.Equal(2, pending.UnsafeReadings);
        Assert.Equal(0, pending.FailedCycles);
        state.CompleteCycle(Unsafe(6), 6);
        Assert.False(state.Snapshot(6).PermitsSafe);
    }

    [Fact] public void SafeObservationClearsPendingUnsafeEvidence() {
        var state = new EndpointSafetyState(Options with { UnsafeReadingsToUnsafe = 3 });
        state.CompleteCycle(Safe(0), 0);
        state.CompleteCycle(Unsafe(2), 2);
        state.CompleteCycle(Unsafe(4), 4);
        state.CompleteCycle(Safe(6), 6);
        Assert.True(state.Snapshot(6).PermitsSafe);
        Assert.Equal(0, state.Snapshot(6).UnsafeReadings);
    }

    [Fact] public void PendingUnsafeDoesNotRefreshCache() {
        var state = new EndpointSafetyState(Options with { UnsafeReadingsToUnsafe = 100 });
        state.CompleteCycle(Safe(0), 0);
        state.CompleteCycle(Unsafe(29), 29);
        Assert.False(state.Snapshot(30).PermitsSafe);
    }

    [Fact] public void ThreeExhaustedCyclesWithdrawBeforeCacheExpiry() {
        var state = new EndpointSafetyState(Options);
        state.CompleteCycle(Safe(0), 0);
        state.CompleteCycle(Failure, 1);
        state.CompleteCycle(Failure, 2);
        Assert.True(state.Snapshot(2).PermitsSafe);
        state.CompleteCycle(Failure, 3);
        Assert.False(state.Snapshot(3).PermitsSafe);
    }

    [Fact] public void HoldAndCountAreBothRequiredAndGettersCannotConfirmRecovery() {
        var state = new EndpointSafetyState(Options with { SafeReadingsToSafe = 3, ReturnToSafeHoldSeconds = 10 });
        state.CompleteCycle(Safe(0), 0);
        state.CompleteCycle(Safe(2), 2);
        state.CompleteCycle(Safe(4), 4);
        Assert.False(state.Snapshot(10).PermitsSafe);
        state.CompleteCycle(Safe(10), 10);
        Assert.True(state.Snapshot(10).PermitsSafe);
    }

    [Fact] public void FailedAttemptResetsRecoveryEvenIfRetrySucceeds() {
        var state = new EndpointSafetyState(Options with { SafeReadingsToSafe = 2, ReturnToSafeHoldSeconds = 10 });
        state.CompleteCycle(Safe(0), 0);
        state.CompleteCycle(Safe(8), 8);
        state.AttemptFailed(9, "timeout");
        state.CompleteCycle(Safe(10), 10);
        Assert.False(state.Snapshot(10).PermitsSafe);
        Assert.Equal(1, state.Snapshot(10).SafeReadings);
        state.CompleteCycle(Safe(20), 20);
        Assert.True(state.Snapshot(20).PermitsSafe);
    }

    [Fact] public void ConfirmedUnsafeCannotBeRestoredByTransportGrace() {
        var state = new EndpointSafetyState(Options);
        state.CompleteCycle(Safe(0), 0);
        state.CompleteCycle(Unsafe(2), 2);
        state.CompleteCycle(Failure, 3);
        Assert.False(state.Snapshot(3).PermitsSafe);
    }

    [Fact] public void PermanentFaultBypassesCounts() {
        var state = new EndpointSafetyState(Options with { FailedCyclesToUnsafe = 100 });
        state.CompleteCycle(Safe(0), 0);
        state.CompleteCycle(new(PollOutcome.PermanentFailure, Reason: "HTTP status 401"), 1);
        Assert.Equal(EndpointPhase.Faulted, state.Snapshot(1).Phase);
        Assert.False(state.Snapshot(1).PermitsSafe);
    }

    [Fact] public void LateSafeResultCannotAuthorizeAlreadyExpiredResponse() {
        var state = new EndpointSafetyState(Options);
        state.CompleteCycle(Safe(0) with { Received = 31 }, 31);
        Assert.False(state.Snapshot(31).PermitsSafe);
    }

    [Fact] public void ExpiryBetweenSafePollsResetsRecoveryHold() {
        var state = new EndpointSafetyState(Options with { SafeReadingsToSafe = 2, ReturnToSafeHoldSeconds = 1 });
        state.CompleteCycle(Safe(0), 0);
        state.CompleteCycle(Safe(1), 1);
        state.CompleteCycle(Safe(32), 32);
        Assert.False(state.Snapshot(32).PermitsSafe);
        Assert.Equal(1, state.Snapshot(32).SafeReadings);
    }

    [Fact] public void BackoffCarriesAcrossCyclesResetsAndHonorsServerDelay() {
        var backoff = new RetryBackoff(Options, () => 1);
        Assert.Equal(.5, backoff.Next().TotalSeconds);
        Assert.Equal(1, backoff.Next().TotalSeconds);
        Assert.Equal(2, backoff.Next().TotalSeconds);
        Assert.Equal(3600, backoff.Next(TimeSpan.FromHours(1)).TotalSeconds);
        for (var i = 0; i < 2000; i++) Assert.InRange(backoff.Next().TotalSeconds, .25, 30);
        backoff.Reset();
        Assert.Equal(.5, backoff.Next().TotalSeconds);
    }

    [Fact] public void ConfigurationRejectsNoSourcesDuplicatesSecretsAndImpossibleFreshness() {
        Assert.Throws<ArgumentException>(() => new SafetyConfiguration().Freeze());
        Assert.Throws<ArgumentException>(() => new SafetyConfiguration { Endpoints = [Options with { Enabled = false }] }.Freeze());
        Assert.Throws<ArgumentException>(() => new SafetyConfiguration { Endpoints = [Options, Options] }.Freeze());
        Assert.Throws<ArgumentException>(() => (Options with { BaseUrl = "https://user:secret@host" }).Validate());
        Assert.Throws<ArgumentException>(() => (Options with { BaseUrl = "https://host?token=secret" }).Validate());
        Assert.Throws<ArgumentException>(() => (Options with { MaximumSafeAgeSeconds = 2 }).Validate());
        Assert.Throws<ArgumentException>(() => (Options with { PollSeconds = double.NaN }).Validate());
        Assert.Throws<ArgumentException>(() => (Options with { CredentialReference = "unsupported" }).Validate());
    }
}
