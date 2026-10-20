namespace Nina.FieldKit.Core.Clouds;

public interface ICloudHoldHost {
    bool IsMorning { get; }
    bool CanResume { get; }
    Task ParkAsync(CancellationToken token);
    Task ResumeAsync(CancellationToken token);
    void ExitImaging(bool morning);
}

public static class CloudHoldWorkflow {
    public static async Task RunAsync(ICloudHoldHost host, Func<CloudSnapshot> snapshot,
        Action<string> retryFailed, TimeProvider clock, CancellationToken token) {
        try {
            await host.ParkAsync(token);
            while (true) {
                token.ThrowIfCancellationRequested();
                var state = snapshot();
                if (host.IsMorning || state.Phase is CloudPhase.Bailed or CloudPhase.Faulted) {
                    host.ExitImaging(host.IsMorning); return;
                }
                if (state.Phase == CloudPhase.RetryAllowed && host.CanResume) {
                    try { await host.ResumeAsync(token); return; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error) {
                        retryFailed("Recovery failed: " + error.GetType().Name);
                        await host.ParkAsync(token);
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(state.RetryInSeconds ?? 5, .1, 5)), clock, token);
            }
        } catch {
            // Never unpark as cleanup. An interrupted retry can leave the mount moving.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await host.ParkAsync(cleanup.Token); } catch { }
            host.ExitImaging(false);
            throw;
        }
    }
}
