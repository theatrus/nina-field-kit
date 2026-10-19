using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Nina.FieldKit.Core.Safety;

namespace Nina.FieldKit.Plugin.Safety;

public interface ILocalSafetyDriver : IDisposable {
    bool Connected { get; set; }
    bool IsSafe { get; }
    int InterfaceVersion { get; }
}

// One STA, with a message pump, owns all driver calls including activation and cleanup.
// Cancellation can stop waiting for COM, but cannot abort an unresponsive native driver.
public sealed class AscomSafetyClient : ISafetyEndpointClient {
    private sealed class NativeDriver(string id) : ILocalSafetyDriver {
        private readonly ASCOM.Com.DriverAccess.SafetyMonitor driver = new(id);
        public bool Connected { get => driver.Connected; set => driver.Connected = value; }
        public bool IsSafe => driver.IsSafe;
        public int InterfaceVersion => driver.InterfaceVersion;
        public void Dispose() => driver.Dispose();
    }
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DriverLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SafetyEndpointOptions options;
    private readonly TimeProvider clock;
    private readonly Func<string, ILocalSafetyDriver> factory;
    private readonly SemaphoreSlim pollGate = new(1, 1);
    private readonly object gate = new();
    private readonly TaskCompletionSource<Dispatcher> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<SafetyPollResult>? pending;
    private Dispatcher? dispatcher;
    private ILocalSafetyDriver? driver;
    private bool started, disposed, connectedHere;
    public Task Completion => completion.Task;
    private double Now => (double)clock.GetTimestamp() / clock.TimestampFrequency;

    public AscomSafetyClient(SafetyEndpointOptions options, TimeProvider? clock = null, Func<string, ILocalSafetyDriver>? factory = null) {
        options.Validate();
        if (options.Kind != SafetySourceKind.Ascom) throw new ArgumentException("ASCOM source required.");
        this.options = options; this.clock = clock ?? TimeProvider.System;
        this.factory = factory ?? (id => new NativeDriver(id));
    }
    private void StartWorker(SemaphoreSlim driverLock) {
        var thread = new Thread(() => {
            try {
                var local = Dispatcher.CurrentDispatcher;
                lock (gate) {
                    dispatcher = local;
                    ready.TrySetResult(local);
                    if (disposed) local.BeginInvokeShutdown(DispatcherPriority.Background);
                }
                Dispatcher.Run();
            } catch (Exception e) { ready.TrySetException(e); }
            finally {
                try { if (connectedHere && driver is not null) driver.Connected = false; } catch { }
                try { driver?.Dispose(); } catch { }
                driverLock.Release();
                completion.TrySetResult();
            }
        }) { IsBackground = true, Name = "Field Kit ASCOM safety" };
        thread.SetApartmentState(ApartmentState.STA);
        try { thread.Start(); } catch { driverLock.Release(); throw; }
    }
    public async Task<SafetyPollResult> PollAsync(CancellationToken token) {
        var start = Now;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.RequestTimeoutSeconds), clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var entered = false;
        try {
            await pollGate.WaitAsync(stop.Token).ConfigureAwait(false); entered = true;
            lock (gate) if (disposed) throw new ObjectDisposedException(nameof(AscomSafetyClient));
            if (!started) {
                var driverLock = DriverLocks.GetOrAdd(options.AscomDriverId, _ => new(1, 1));
                await driverLock.WaitAsync(stop.Token).ConfigureAwait(false);
                lock (gate) {
                    if (disposed) { driverLock.Release(); throw new ObjectDisposedException(nameof(AscomSafetyClient)); }
                    started = true;
                    StartWorker(driverLock);
                }
            }
            if (pending is { IsCompleted: false })
                return new(PollOutcome.TransientFailure, Reason: "ASCOM driver is still responding to an earlier call", RequestStarted: start, Received: Now);
            var worker = await ready.Task.WaitAsync(stop.Token).ConfigureAwait(false);
            // Discard any late result; each accepted observation must come from this fresh request.
            pending = worker.InvokeAsync(() => Read(start), DispatcherPriority.Normal).Task;
            return await pending.WaitAsync(stop.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (!token.IsCancellationRequested) {
            return new(PollOutcome.TransientFailure, Reason: "ASCOM request timed out", RequestStarted: start, Received: Now);
        } finally { if (entered) pollGate.Release(); }
    }
    private SafetyPollResult Read(double start) {
        try {
            lock (gate) if (disposed) return new(PollOutcome.PermanentFailure, Reason: "ASCOM source retired", RequestStarted: start, Received: Now);
            driver ??= factory(options.AscomDriverId);
            if (!driver.Connected) {
                if (options.ConnectionPolicy != ConnectionPolicy.Managed)
                    return new(PollOutcome.PermanentFailure, Reason: "ASCOM driver is disconnected; enable driver connection", RequestStarted: start, Received: Now, Connected: false);
                driver.Connected = true; connectedHere = true;
            }
            if (!driver.Connected) return new(PollOutcome.TransientFailure, Reason: "ASCOM driver did not connect", RequestStarted: start, Received: Now, Connected: false);
            var version = driver.InterfaceVersion;
            var safe = driver.IsSafe;
            return new(PollOutcome.Observation, safe, safe ? "ASCOM safe" : "ASCOM unsafe", start, Now, InterfaceVersion: version, Connected: true);
        } catch (Exception e) {
            return new(e is COMException { ErrorCode: unchecked((int)0x80040154) } ? PollOutcome.PermanentFailure : PollOutcome.TransientFailure,
                Reason: "ASCOM driver error (" + e.GetType().Name + ")", RequestStarted: start, Received: Now);
        }
    }
    public void Dispose() {
        lock (gate) {
            if (disposed) return;
            disposed = true;
            if (!started) completion.TrySetResult();
            dispatcher?.BeginInvokeShutdown(DispatcherPriority.Background);
        }
        // Do not wait for COM cleanup on NINA's UI or safety worker thread.
    }
}
