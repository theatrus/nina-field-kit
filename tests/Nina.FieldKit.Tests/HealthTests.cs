using Nina.FieldKit.Core;
using Xunit;

namespace Nina.FieldKit.Tests;

public sealed class HealthTests {
    internal static MountObservation Connected => new(DateTimeOffset.UtcNow, TimeSpan.Zero,
        ObservationSource.CachedMediator, Connected: true, TrackingEnabled: true,
        AtPark: false, AtHome: false, Slewing: false);

    [Fact] public void HealthyCachedStateDoesNotProveTransport() {
        var result = MountHealthEvaluator.Evaluate(Connected, new(RequireConfirmedResponse: true));
        Assert.Equal(HealthStatus.Unknown, result.Status);
        Assert.Contains(result.Findings, f => f.Code == "ResponseFreshnessUnknown");
    }

    [Fact] public void TrackingOffIsAllowedUnlessExplicitlyRequired() {
        var stopped = Connected with { TrackingEnabled = false };
        Assert.Equal(HealthStatus.Healthy, MountHealthEvaluator.Evaluate(stopped, new()).Status);
        Assert.Equal(HealthStatus.Unhealthy, MountHealthEvaluator.Evaluate(stopped, new(RequireTracking: true)).Status);
    }

    [Fact] public void DisconnectionIsAFaultEvenWhenFreshnessIsUnknown() {
        var result = MountHealthEvaluator.Evaluate(Connected with { Connected = false }, new(RequireConfirmedResponse: true));
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact] public void MissingTrackingStateIsUnknown() {
        Assert.Equal(HealthStatus.Unknown, MountHealthEvaluator.Evaluate(Connected with { TrackingEnabled = null }, new(RequireTracking: true)).Status);
    }

    [Fact] public void AsiHomeSlewConflictIsUnresolved() {
        var result = MountHealthEvaluator.Evaluate(Connected with { AtHome = true, Slewing = true, DeclinationDegrees = 90 }, new());
        Assert.Equal(HealthStatus.Unknown, result.Status);
        Assert.Contains(result.Findings, f => f.Code == "HomeStateConflict");
    }

    [Fact] public void PoleCoordinatesAloneAreNotAReset() {
        Assert.Equal(HealthStatus.Healthy, MountHealthEvaluator.Evaluate(Connected with { AtHome = true, DeclinationDegrees = 90 }, new()).Status);
    }

    [Fact] public void LaterGoodStateDoesNotEraseTrackingLoss() {
        Assert.Equal(HealthStatus.Unhealthy, MountHealthEvaluator.EvaluateWindow(
            [Connected with { TrackingEnabled = false }, Connected], new(RequireTracking: true)).Status);
    }

    [Fact] public void ReadFailureDoesNotPass() {
        Assert.Equal(HealthStatus.Unknown, MountHealthEvaluator.Evaluate(Connected with { ReadError = "Synthetic read error" }, new()).Status);
    }

    [Fact] public void EmptyWindowIsUnknown() => Assert.Equal(HealthStatus.Unknown, MountHealthEvaluator.EvaluateWindow([], new()).Status);

    [Theory] [InlineData(0)] [InlineData(61)]
    public async Task InvalidWindowDoesNotRead(int count) {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ObservationWindow.CaptureAsync(
            () => throw new InvalidOperationException("Must not read"), count, CancellationToken.None));
    }

    [Fact] public async Task CancellationBeforeCaptureDoesNotRead() {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ObservationWindow.CaptureAsync(
            () => throw new InvalidOperationException("Must not read"), 3, new CancellationToken(true)));
    }

    [Fact] public async Task CancellationDuringWaitPreventsAnotherRead() {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var task = ObservationWindow.CaptureAsync(() => { calls++; return Connected; }, 3, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, calls);
    }
}
