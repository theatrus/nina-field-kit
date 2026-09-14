namespace Nina.FieldKit.Core.Safety;

public sealed record SafetyMonitorSnapshot(Guid Revision, Guid Generation, bool Connected, bool IsSafe,
    string Summary, IReadOnlyList<EndpointSafetySnapshot> Endpoints,
    bool RetainingPreviousResult = false, double? RetainedSafeSecondsRemaining = null);

public sealed class SafetyMonitorService : IDisposable {
    private sealed class Worker(SafetyEndpointOptions options, ISafetyEndpointClient client) {
        public SafetyEndpointOptions Options { get; } = options;
        public ISafetyEndpointClient Client { get; } = client;
        public EndpointSafetyState State { get; } = new(options);
        public double? NextRequest;
        public int Attempt;
        public bool CheckInProgress;
        public string? LastLog;
        public SafetyPollResult? LastPoll;
        public long CompletedPolls, FailedPolls;
    }

    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly SafetyConfiguration configuration;
    private readonly Guid generation = Guid.NewGuid();
    private readonly List<Worker> workers = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim concurrency = new(4, 4);
    private readonly Action<string, object>? log;
    private readonly Func<SafetyEndpointOptions, ISafetyEndpointClient>? clientFactory;
    private Task startAfter = Task.CompletedTask;
    private double? preservedSafeUntil;
    private bool active, started, disposed, aggregateSafe, serviceFault;
    private bool? lastLoggedSafe;
    private Task completion = Task.CompletedTask;
    public Task Completion => completion;
    public event Action? Changed;

    public SafetyMonitorService(SafetyConfiguration configuration, TimeProvider? clock = null,
        Func<SafetyEndpointOptions, ISafetyEndpointClient>? clientFactory = null, Action<string, object>? log = null) {
        this.configuration = configuration.Freeze();
        this.clock = clock ?? TimeProvider.System;
        this.log = log;
        this.clientFactory = clientFactory;
        try {
            foreach (var options in this.configuration.Endpoints.Where(e => e.Enabled))
                workers.Add(new(options, (clientFactory ?? (o => new AlpacaSafetyClient(o, this.clock)))(options)));
        } catch {
            foreach (var worker in workers) worker.Client.Dispose();
            cancellation.Dispose();
            concurrency.Dispose();
            throw;
        }
    }

    private double Now => (double)clock.GetTimestamp() / clock.TimestampFrequency;

    public SafetyMonitorService CreateReplacement(SafetyConfiguration next) {
        lock (gate) {
            var previous = Snapshot();
            var replacement = new SafetyMonitorService(next, clock, clientFactory, log) { startAfter = completion };
            if (previous.IsSafe) {
                // The original deadline survives repeated edits. Saving never refreshes evidence.
                replacement.preservedSafeUntil = preservedSafeUntil ?? Now + previous.Endpoints.Min(e =>
                    Math.Max(0, workers.Single(w => w.Options.Id == e.Id).Options.MaximumSafeAgeSeconds - (e.SafeAgeSeconds ?? double.PositiveInfinity)));
            }
            return replacement;
        }
    }

    public void Start() {
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) throw new InvalidOperationException("A service generation may only be started once.");
            active = started = true;
            completion = Task.WhenAll(workers.Select(w => Task.Run(() => RunWorkerAsync(w)))
                .Append(Task.Run(ExpiryLoopAsync)));
        }
        Write("Connected", new { configuration.Revision, generation,
            Policies = configuration.Endpoints.Where(e => e.Enabled).Select(e => new {
                e.Id, e.PollSeconds, e.RequestTimeoutSeconds, e.AttemptsPerCycle, e.InitialBackoffSeconds,
                e.BackoffMultiplier, e.BackoffCapSeconds, e.MaximumSafeAgeSeconds, e.ConnectionLifetimeSeconds,
                e.FailedCyclesToUnsafe, e.UnsafeReadingsToUnsafe, e.SafeReadingsToSafe, e.ReturnToSafeHoldSeconds, e.ConnectionPolicy
            }) });
        Notify();
    }

    public SafetyMonitorSnapshot Snapshot() {
        lock (gate) {
            if (!active) return new(configuration.Revision, generation, false, false, "Disconnected; safety history cleared", []);
            var now = Now;
            var snapshots = workers.Select(w => w.State.Snapshot(now) with {
                NextRequestInSeconds = w.NextRequest is double next ? Math.Max(0, next - now) : null,
                Attempt = w.Attempt, LastPoll = w.LastPoll,
                LastPollAgeSeconds = w.LastPoll is { } poll ? Math.Max(0, now - poll.Received) : null,
                CompletedPolls = w.CompletedPolls, FailedPolls = w.FailedPolls, CheckInProgress = w.CheckInProgress
            }).ToArray();
            if (serviceFault || snapshots.Length == 0 || snapshots.Any(s => !s.PermitsSafe)) aggregateSafe = false;
            else if (!aggregateSafe)
                aggregateSafe = snapshots.All(s => s.Phase == EndpointPhase.FreshSafe && s.RecoveryConfirmed);
            var pending = snapshots.Any(s => s.Phase != EndpointPhase.FreshSafe);
            var preserving = false;
            if (preservedSafeUntil is double deadline) {
                if (serviceFault || now >= deadline || snapshots.Any(s => s.Phase is EndpointPhase.Unsafe or EndpointPhase.Faulted or EndpointPhase.Stale) ||
                    snapshots.All(s => s.Phase == EndpointPhase.FreshSafe && s.RecoveryConfirmed)) preservedSafeUntil = null;
                else { aggregateSafe = preserving = true; }
            }
            return new(configuration.Revision, generation, true, aggregateSafe,
                preserving ? $"Safe: retaining previous result while updated sources refresh (expires in {Math.Max(0, preservedSafeUntil!.Value - now):F1} s)" :
                serviceFault ? "Unsafe: local worker fault" : aggregateSafe ? pending ? "Safe pending confirmation" : "Safe"
                : "Unsafe: " + string.Join("; ", snapshots.Where(s => !s.PermitsSafe || s.Phase != EndpointPhase.FreshSafe || !s.RecoveryConfirmed)
                    .Select(s => $"{s.Label}: {s.Reason}")), snapshots, preserving,
                    preserving ? Math.Max(0, preservedSafeUntil!.Value - now) : null);
        }
    }

    private async Task RunWorkerAsync(Worker worker) {
        var token = cancellation.Token;
        var backoff = new RetryBackoff(worker.Options);
        try {
            await startAfter.WaitAsync(token).ConfigureAwait(false);
            while (!token.IsCancellationRequested) {
                var nextCycle = TimeSpan.FromSeconds(worker.Options.PollSeconds);
                for (var attempt = 1; attempt <= worker.Options.AttemptsPerCycle; attempt++) {
                    lock (gate) { if (!active) return; worker.Attempt = attempt; worker.NextRequest = null; worker.CheckInProgress = true; }
                    SafetyPollResult result;
                    await concurrency.WaitAsync(token).ConfigureAwait(false);
                    try {
                        result = await worker.Client.PollAsync(token).ConfigureAwait(false);
                    } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                        throw;
                    } catch (Exception) {
                        // Exception text may contain credential-bearing URLs. Do not forward it into diagnostics.
                        result = new(PollOutcome.PermanentFailure, Reason: "Unexpected endpoint-client failure", RequestStarted: Now, Received: Now);
                    } finally {
                        concurrency.Release();
                    }
                    token.ThrowIfCancellationRequested();
                    lock (gate) {
                        if (!active) return; // Retired generations never accept late results.
                        worker.CheckInProgress = result.Outcome == PollOutcome.TransientFailure && attempt < worker.Options.AttemptsPerCycle;
                        worker.LastPoll = result;
                        worker.CompletedPolls++;
                        if (result.Outcome != PollOutcome.Observation) worker.FailedPolls++;
                        if (result.Outcome == PollOutcome.TransientFailure) {
                            worker.State.AttemptFailed(Now, result.Reason);
                            if (attempt == worker.Options.AttemptsPerCycle) worker.State.CompleteCycle(result, Now);
                        } else worker.State.CompleteCycle(result, Now);
                    }
                    Write("PollCompleted", new { configuration.Revision, generation, worker.Options.Id, worker.Options.Label,
                        attempt, result.Outcome, result.IsSafe, result.Reason, result.InterfaceVersion, result.Connected,
                        result.ErrorNumber, result.ServerTransactionId, latencySeconds = result.Received - result.RequestStarted });
                    PublishTransitions();
                    if (result.Outcome == PollOutcome.Observation) {
                        if (attempt > 1) Write("CheckRecovered", new { worker.Options.Id, worker.Options.Label, attempt, result.IsSafe,
                            reason = "Check completed after retry; this check was not missed" });
                        backoff.Reset(); break;
                    }
                    if (result.Outcome == PollOutcome.PermanentFailure) {
                        nextCycle = TimeSpan.FromSeconds(worker.Options.BackoffCapSeconds);
                        break;
                    }
                    var delay = backoff.Next(result.RetryAfter);
                    Write(attempt == worker.Options.AttemptsPerCycle ? "CheckMissed" : "RetryScheduled", new { configuration.Revision, generation, worker.Options.Id, worker.Options.Label, attempt, attemptsAllowed = worker.Options.AttemptsPerCycle,
                        delaySeconds = (attempt == worker.Options.AttemptsPerCycle && nextCycle > delay ? nextCycle : delay).TotalSeconds,
                        retryAfterSeconds = result.RetryAfter?.TotalSeconds, cycleExhausted = attempt == worker.Options.AttemptsPerCycle });
                    if (attempt == worker.Options.AttemptsPerCycle) {
                        nextCycle = delay > nextCycle ? delay : nextCycle;
                        break;
                    }
                    await WaitAsync(worker, delay, token).ConfigureAwait(false);
                }
                await WaitAsync(worker, nextCycle, token).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (token.IsCancellationRequested) {
        } catch (Exception) {
            lock (gate) {
                if (active) { serviceFault = true; aggregateSafe = false; }
            }
            Write("ServiceFault", new { configuration.Revision, generation, worker.Options.Id });
            Notify();
        }
    }

    private async Task WaitAsync(Worker worker, TimeSpan delay, CancellationToken token) {
        var deadline = Now + delay.TotalSeconds;
        lock (gate) { worker.NextRequest = deadline; }
        Notify();
        // Retry-After may exceed the timer API's maximum. Wait in cancellable slices;
        // the independent expiry scheduler and getters continue to withdraw safety.
        while (true) {
            token.ThrowIfCancellationRequested();
            var remaining = deadline - Now;
            if (remaining <= 0) return;
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(remaining, .001, 60)), clock, token).ConfigureAwait(false);
        }
    }

    private async Task ExpiryLoopAsync() {
        try {
            while (!cancellation.IsCancellationRequested) {
                await Task.Delay(TimeSpan.FromMilliseconds(250), clock, cancellation.Token).ConfigureAwait(false);
                PublishTransitions();
            }
        } catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
        } catch (Exception) {
            lock (gate) { serviceFault = true; aggregateSafe = false; }
            Write("ServiceFault", new { configuration.Revision, generation });
            Notify();
        }
    }

    private void PublishTransitions() {
        SafetyMonitorSnapshot snapshot;
        var changed = new List<EndpointSafetySnapshot>();
        bool aggregateChanged;
        lock (gate) {
            if (!active) return;
            snapshot = Snapshot();
            aggregateChanged = lastLoggedSafe != snapshot.IsSafe;
            lastLoggedSafe = snapshot.IsSafe;
            foreach (var endpoint in snapshot.Endpoints) {
                var worker = workers.Single(w => w.Options.Id == endpoint.Id);
                var key = $"{endpoint.Phase}:{endpoint.PermitsSafe}:{endpoint.Reason}";
                if (worker.LastLog != key) { worker.LastLog = key; changed.Add(endpoint); }
            }
        }
        foreach (var endpoint in changed) Write("EndpointTransition", new { configuration.Revision, generation, endpoint });
        if (aggregateChanged) Write("AggregateTransition", new { configuration.Revision, generation, snapshot.IsSafe, snapshot.Summary });
        Notify();
    }

    private void Write(string transition, object evidence) {
        try { log?.Invoke(transition, evidence); } catch { /* A logging sink cannot preserve or authorize safety. */ }
    }
    private void Notify() {
        try { Changed?.Invoke(); } catch { /* UI observers do not own safety evaluation. */ }
    }

    public void Dispose() {
        lock (gate) {
            if (disposed) return;
            disposed = true;
            active = aggregateSafe = false;
        }
        cancellation.Cancel();
        foreach (var worker in workers) worker.Client.Dispose();
        _ = completion.ContinueWith(_ => {
            cancellation.Dispose();
            concurrency.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Write("Disconnected", new { configuration.Revision, generation });
        Notify();
    }
}
