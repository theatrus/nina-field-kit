using System.IO;
using Microsoft.Extensions.Time.Testing;
using Nina.FieldKit.Core.Clouds;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class CloudDetectorTests {
    private static readonly CloudFrameKey Key = new("M31", "L", 60, 1, 1, 100, 10, 1000, 800);
    private static CloudDetector Ready(CloudOptions? options = null) {
        var d = new CloudDetector(options); d.Arm(); d.SetScope(Key.Target, Key.Filter);
        for (var i = 0; i < 3; i++) d.ObserveFrame(Key, 100, i, i);
        return d;
    }
    [Fact] public void DropNeedsConfirmedComparableFrames() {
        var d = Ready();
        d.ObserveFrame(Key, 20, 3, 3); d.ObserveFrame(Key, 20, 4, 4);
        Assert.True(d.Snapshot(4).IsSafe);
        d.ObserveFrame(Key, 20, 5, 5);
        Assert.Equal(CloudPhase.Holding, d.Snapshot(5).Phase);
        Assert.Equal(100, d.Snapshot(5).BaselineStars);
        Assert.False(d.Snapshot(5).IsSafe);
    }
    [Fact] public void CountsAreIsolatedByTargetFilterAndAcquisitionSettings() {
        foreach (var other in new[] { Key with { Target = "M42" }, Key with { Filter = "Ha" }, Key with { ExposureSeconds = 1 },
            Key with { BinX = 2 }, Key with { Gain = 200 }, Key with { Offset = 20 }, Key with { Width = 200 } }) {
            var d = Ready();
            for (var i = 3; i < 6; i++) d.ObserveFrame(other, 5, i, i);
            Assert.True(d.Snapshot(6).IsSafe);
            Assert.NotEqual(CloudPhase.Holding, d.Snapshot(6).Phase);
        }
    }
    [Fact] public void HealthyReferenceNeverLearnsACloudyDecline() {
        var d = Ready(new() { BadFrames = 30 });
        for (var i = 3; i < 20; i++) d.ObserveFrame(Key, 40, i, i);
        Assert.Equal(100, d.Snapshot(20).BaselineStars);
    }
    [Fact] public void TimerAllowsTrialButDoesNotConfirmClearSkies() {
        var d = Ready(new() { RetrySeconds = 10, MaximumEpisodeSeconds = 100 });
        d.Trip("Clouds", 5);
        Assert.False(d.Snapshot(14).IsSafe);
        var trial = d.Snapshot(15);
        Assert.Equal(CloudPhase.RetryAllowed, trial.Phase);
        Assert.True(trial.IsSafe);
        Assert.Contains("not yet confirmed", trial.Reason);
        d.ObserveFrame(Key, 10, 16, 16);
        Assert.Equal(CloudPhase.Holding, d.Snapshot(16).Phase);
        Assert.Equal(11, d.Snapshot(16).EpisodeSeconds);
    }
    [Fact] public void StaleFramesAndScopeChangesCannotClearAnEvent() {
        var d = Ready(new() { RetrySeconds = 10 });
        d.Trip("Clouds", 5);
        d.SetScope("M42", "Ha");
        Assert.False(d.Snapshot(6).IsSafe);
        d.SetScope(Key.Target, Key.Filter);
        d.Tick(15);
        d.ObserveFrame(Key, 100, 4, 16); d.ObserveFrame(Key, 100, 4, 17);
        Assert.Equal(CloudPhase.RetryAllowed, d.Snapshot(17).Phase);
        Assert.Equal(0, d.Snapshot(17).ClearFrames);
        d.ObserveFrame(Key, 100, 18, 18); d.ObserveFrame(Key, 100, 19, 19);
        Assert.Equal(CloudPhase.Monitoring, d.Snapshot(19).Phase);
        Assert.Null(d.Snapshot(19).EpisodeSeconds);
    }
    [Fact] public void PreArmImagesAreNotAccepted() {
        var d = new CloudDetector(); d.Arm(100); d.SetScope(Key.Target, Key.Filter);
        for (var i = 0; i < 4; i++) d.ObserveFrame(Key, 0, 99, 101 + i);
        Assert.Equal(CloudPhase.Learning, d.Snapshot(105).Phase);
        Assert.Null(d.Snapshot(105).Stars);
    }
    [Fact] public void ZeroStarsCanTripDuringStartupButMissingAnalysisDoesNotBecomeAReference() {
        var d = new CloudDetector(); d.Arm(); d.SetScope(Key.Target, Key.Filter);
        for (var i = 0; i < 3; i++) d.ObserveFrame(Key, 0, i, i);
        Assert.False(d.Snapshot(3).IsSafe);
        Assert.Null(d.Snapshot(3).BaselineStars);
    }
    [Fact] public void LostGuidingIsDebouncedAndFlappingUsesTransitions() {
        var d = Ready();
        d.ObserveGuiding(false, false, 5); d.ObserveGuiding(false, false, 20);
        Assert.True(d.Snapshot(20).IsSafe);
        d.ObserveGuiding(false, false, 35);
        Assert.False(d.Snapshot(35).IsSafe);
        d = Ready();
        for (var i = 0; i < 2; i++) { d.ObserveGuiding(false, false, 5 + i * 2); d.ObserveGuiding(true, false, 6 + i * 2); }
        Assert.True(d.Snapshot(9).IsSafe);
        d.ObserveGuiding(false, false, 10);
        Assert.Contains("repeatedly", d.Snapshot(10).Reason);
    }
    [Fact] public void RecoveryRequiresConsecutiveClearFrames() {
        var d = Ready(new() { RetrySeconds = 10 }); d.Trip("Clouds", 5); d.Tick(15);
        d.ObserveFrame(Key, 90, 16, 16);
        d.ObserveFrame(Key, 60, 17, 17);
        d.ObserveFrame(Key, 90, 18, 18);
        Assert.Equal(CloudPhase.RetryAllowed, d.Snapshot(18).Phase);
        Assert.Equal(1, d.Snapshot(18).ClearFrames);
        d.ObserveFrame(Key, 90, 19, 19);
        Assert.Equal(CloudPhase.Monitoring, d.Snapshot(19).Phase);
    }
    [Fact] public void StartupZeroCountsMustBeConsecutiveAndTrialZeroHoldsImmediately() {
        var d = new CloudDetector(new() { RetrySeconds = 10 }); d.Arm(); d.SetScope(Key.Target, Key.Filter);
        d.ObserveFrame(Key, 0, 0, 0); d.ObserveFrame(Key, 100, 1, 1);
        d.ObserveFrame(Key, 0, 2, 2); d.ObserveFrame(Key, 100, 3, 3); d.ObserveFrame(Key, 0, 4, 4);
        Assert.True(d.Snapshot(4).IsSafe);
        d.Trip("Guiding lost", 5); d.Tick(15); d.ObserveFrame(Key, 0, 16, 16);
        Assert.Equal(CloudPhase.Holding, d.Snapshot(16).Phase);
    }
    [Fact] public void IntentionalPausesAndDisabledGuideDetectionDoNotTrip() {
        var d = Ready();
        d.ObserveGuiding(false, true, 5); d.ObserveGuiding(false, true, 100);
        Assert.True(d.Snapshot(100).IsSafe);
        d = Ready(new() { UseGuiding = false });
        d.ObserveGuiding(false, false, 5); d.ObserveGuiding(false, false, 100);
        Assert.True(d.Snapshot(100).IsSafe);
    }
    [Fact] public void SolveSuccessResetsFailuresAndTrialFailureHoldsImmediately() {
        var d = Ready(new() { RetrySeconds = 10 });
        d.ObserveSolve(false, 5); d.ObserveSolve(true, 6); d.ObserveSolve(false, 7);
        Assert.True(d.Snapshot(7).IsSafe);
        d.ObserveSolve(false, 8);
        Assert.False(d.Snapshot(8).IsSafe);
        d.Tick(18); d.ObserveSolve(false, 19);
        Assert.False(d.Snapshot(19).IsSafe);
    }
    [Fact] public void RetryBudgetIncludesTrialsAndCannotBeResetByScopeOrMoreBadEvidence() {
        var d = Ready(new() { RetrySeconds = 10, MaximumEpisodeSeconds = 30 });
        d.Trip("Cloud", 5); d.Tick(15); d.Trip("Again", 16);
        d.SetScope("Other", "R"); d.Tick(26); d.Trip("Again", 27);
        Assert.Equal(CloudPhase.Bailed, d.Snapshot(35).Phase);
        Assert.False(d.Snapshot(100).IsSafe);
    }
    [Fact] public void ActualParkingStartsOwnHoldTimerWithoutExtendingTheOverallBudget() {
        var d = Ready(new() { RetrySeconds = 10, MaximumEpisodeSeconds = 30 });
        d.Trip("Cloud", 5); d.Tick(15); d.BeginParkedHold(20);
        Assert.False(d.Snapshot(29).IsSafe);
        Assert.Equal(CloudPhase.RetryAllowed, d.Snapshot(30).Phase);
        Assert.Equal(CloudPhase.Bailed, d.Snapshot(35).Phase);
    }
    [Fact] public void FaultAndDisarmAreUnsafe() {
        var d = Ready(); d.Fault("Observer failed");
        Assert.False(d.Snapshot(10).IsSafe);
        d.Tick(10000); Assert.Equal(CloudPhase.Faulted, d.Snapshot(10000).Phase);
        d.Disarm(); Assert.False(d.Snapshot(10000).IsSafe);
    }
    [Theory] [InlineData(0)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void InvalidRetryConfigurationIsRejected(double seconds) => Assert.Throws<ArgumentException>(() => new CloudOptions { RetrySeconds = seconds }.Validate());

    private sealed class Host : ICloudHoldHost {
        public bool IsMorning { get; set; }
        public bool CanResume { get; set; } = true;
        public readonly List<string> Calls = new();
        public Action? Resuming;
        public Task ParkAsync(CancellationToken token) { Calls.Add("park"); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task ResumeAsync(CancellationToken token) { Calls.Add("resume"); Resuming?.Invoke(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public void ExitImaging(bool morning) => Calls.Add(morning ? "morning" : "exit");
    }
    [Fact] public async Task WorkflowWaitsParkedAndResumesOnlyAfterTimerAndExternalSafety() {
        var time = new FakeTimeProvider(); var host = new Host { CanResume = false };
        var d = Ready(new() { RetrySeconds = 10, MaximumEpisodeSeconds = 100 }); d.Trip("Cloud", 5);
        double Now() => 5 + time.GetUtcNow().Subtract(DateTimeOffset.Parse("2000-01-01T00:00:00Z")).TotalSeconds;
        var run = CloudHoldWorkflow.RunAsync(host, () => d.Snapshot(Now()), why => d.Trip(why, Now()), time, default);
        Assert.Equal(new[] { "park" }, host.Calls);
        time.Advance(TimeSpan.FromSeconds(10)); await Task.Delay(30);
        Assert.DoesNotContain("resume", host.Calls);
        host.CanResume = true; time.Advance(TimeSpan.FromSeconds(5));
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "park", "resume" }, host.Calls);
    }
    [Fact] public async Task MorningBudgetAndCancellationLeaveMountParked() {
        foreach (var morning in new[] { true, false }) {
            var d = Ready(); d.Trip("Cloud", 5);
            if (!morning) d.Bail("Budget");
            var host = new Host { IsMorning = morning };
            await CloudHoldWorkflow.RunAsync(host, () => d.Snapshot(6), _ => { }, TimeProvider.System, default);
            Assert.Equal(new[] { "park", morning ? "morning" : "exit" }, host.Calls);
        }
        var clock = new FakeTimeProvider(); var cancelled = new Host(); var detector = Ready(); detector.Trip("Cloud", 5);
        using var stop = new CancellationTokenSource();
        var run = CloudHoldWorkflow.RunAsync(cancelled, () => detector.Snapshot(6), _ => { }, clock, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.DoesNotContain("resume", cancelled.Calls);
        Assert.Equal("exit", cancelled.Calls.Last());
    }
    [Fact] public async Task FailedResumeParksAgainAndDoesNotBusyLoop() {
        var clock = new FakeTimeProvider(); var host = new Host { Resuming = () => throw new IOException("Synthetic") };
        var detector = Ready(new() { RetrySeconds = 10 }); detector.Trip("Cloud", 5);
        using var stop = new CancellationTokenSource(); var now = 15d;
        var run = CloudHoldWorkflow.RunAsync(host, () => detector.Snapshot(now), why => detector.Trip(why, now), clock, stop.Token);
        Assert.Equal(new[] { "park", "resume", "park" }, host.Calls);
        Assert.False(run.IsCompleted);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}
