using System.Diagnostics;
using Nina.FieldKit.Core;
using NINA.Equipment.Interfaces.Mediator;

namespace Nina.FieldKit.Plugin;

public sealed class MountSnapshotReader(ITelescopeMediator telescope) {
    public MountObservation Capture() {
        var capturedAt = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        try {
            // GetInfo returns mutable cached state. Copy values; never retain TelescopeInfo.
            // The copy is best-effort, not an atomic device transaction.
            var info = telescope.GetInfo();
            if (info is null)
                return new(capturedAt, timer.Elapsed, ObservationSource.CachedMediator, ReadError: "No telescope information is available.");
            if (!info.Connected)
                return new(capturedAt, timer.Elapsed, ObservationSource.CachedMediator, Connected: false);
            var observation = new MountObservation(capturedAt, TimeSpan.Zero, ObservationSource.CachedMediator,
                Connected: true, TrackingEnabled: info.TrackingEnabled,
                TrackingMode: info.TrackingRate.TrackingMode.ToString(), Slewing: info.Slewing,
                AtHome: info.AtHome, AtPark: info.AtPark,
                RightAscensionHours: Finite(info.RightAscension), DeclinationDegrees: Finite(info.Declination),
                Epoch: info.HasUnknownEpoch ? null : info.EquatorialSystem.ToString(),
                PierSide: info.SideOfPier.ToString());
            return observation with { ReadDuration = timer.Elapsed };
        } catch (Exception exception) {
            return new(capturedAt, timer.Elapsed, ObservationSource.CachedMediator, ReadError: exception.ToString());
        }
    }

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}
