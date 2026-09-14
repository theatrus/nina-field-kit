namespace Nina.FieldKit.Core;

public static class ObservationWindow {
    public static async Task<IReadOnlyList<MountObservation>> CaptureAsync(
        Func<MountObservation> capture, int sampleCount, CancellationToken token,
        TimeProvider? timeProvider = null) {
        if (sampleCount is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(sampleCount), "Use 1–60 samples.");
        var samples = new List<MountObservation>(sampleCount);
        for (var index = 0; index < sampleCount; index++) {
            token.ThrowIfCancellationRequested();
            samples.Add(capture());
            token.ThrowIfCancellationRequested();
            if (index + 1 < sampleCount)
                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider ?? TimeProvider.System, token);
        }
        return samples.AsReadOnly();
    }
}
