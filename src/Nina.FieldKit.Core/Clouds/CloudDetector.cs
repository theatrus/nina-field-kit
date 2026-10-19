namespace Nina.FieldKit.Core.Clouds;

public enum CloudPhase { Disarmed, Learning, Monitoring, Holding, RetryAllowed, Bailed, Faulted }
public enum CloudResponse { ParkAndRetry, ReportUnsafe }
public enum MorningBoundary { Astronomical, Nautical, Civil, Sunrise }

public sealed record CloudOptions {
    public double StarFraction { get; init; } = .5;
    public double RecoveryStarFraction { get; init; } = .8;
    public int BaselineFrames { get; init; } = 3;
    public int BadFrames { get; init; } = 3;
    public int ClearFrames { get; init; } = 2;
    public bool UseGuiding { get; init; } = true;
    public double GuideLossSeconds { get; init; } = 30;
    public int GuideLosses { get; init; } = 3;
    public double FlapWindowSeconds { get; init; } = 300;
    public int SolveFailures { get; init; } = 2;
    public double RetrySeconds { get; init; } = 300;
    public double MaximumEpisodeSeconds { get; init; } = 3600;
    public MorningBoundary Morning { get; init; } = MorningBoundary.Nautical;
    public double OperationTimeoutSeconds { get; init; } = 180;
    public double MinimumAltitudeDegrees { get; init; } = 20;

    public CloudOptions Validate() {
        static void Range(double v, double min, double max, string name) {
            if (!double.IsFinite(v) || v < min || v > max) throw new ArgumentException($"{name} must be {min}–{max}.");
        }
        Range(StarFraction, .05, .95, nameof(StarFraction));
        Range(RecoveryStarFraction, StarFraction, 1, nameof(RecoveryStarFraction));
        Range(GuideLossSeconds, 1, 600, nameof(GuideLossSeconds));
        Range(FlapWindowSeconds, 10, 3600, nameof(FlapWindowSeconds));
        Range(RetrySeconds, 1, 3600, nameof(RetrySeconds));
        Range(MaximumEpisodeSeconds, RetrySeconds, 43200, nameof(MaximumEpisodeSeconds));
        Range(OperationTimeoutSeconds, 5, 1800, nameof(OperationTimeoutSeconds));
        Range(MinimumAltitudeDegrees, 0, 90, nameof(MinimumAltitudeDegrees));
        if (BaselineFrames is < 3 or > 30 || BadFrames is < 1 or > 30 || ClearFrames is < 1 or > 30 ||
            GuideLosses is < 2 or > 30 || SolveFailures is < 1 or > 30 || !Enum.IsDefined(Morning))
            throw new ArgumentException("Invalid cloud confirmation counts or morning boundary.");
        return this;
    }
}

public sealed record CloudFrameKey(string Target, string Filter, double ExposureSeconds, int BinX, int BinY,
    int Gain, int Offset, int Width, int Height, int ReadoutMode = 0, string CameraId = "");
public sealed record CloudSnapshot(CloudPhase Phase, bool IsSafe, string Reason, string? Target, string? Filter,
    double? BaselineStars, int? Stars, int LowFrames, int SolveFailures, int GuideLosses,
    int ClearFrames, double? RetryInSeconds, double? EpisodeSeconds, long Revision);

// The caller serializes observations. All times are monotonic seconds; no hardware is accessed here.
public sealed class CloudDetector {
    private sealed class Baseline {
        public readonly List<int> Initial = new();
        public double? Stars;
        public int Low;
    }
    private readonly Dictionary<CloudFrameKey, Baseline> baselines = new();
    private readonly Queue<double> guideLosses = new();
    private CloudOptions options;
    private CloudPhase phase;
    private string reason = "Cloud guard is not armed";
    private string? target, filter;
    private CloudFrameKey? activeKey;
    private int? stars;
    private int solves, clear;
    private bool guideWasLost;
    private double? lostSince, episodeStart, retryAt;
    private double acceptFramesAfter;
    private long revision;

    public CloudDetector(CloudOptions? options = null) => this.options = (options ?? new()).Validate();
    public void Configure(CloudOptions value) => options = value.Validate();
    public void Arm(double now = 0) {
        baselines.Clear(); guideLosses.Clear();
        target = filter = null; activeKey = null; stars = null; solves = clear = 0;
        guideWasLost = false; lostSince = episodeStart = retryAt = null; acceptFramesAfter = now;
        SetPhase(CloudPhase.Learning, "Waiting for comparable light frames");
    }
    public void Disarm() => SetPhase(CloudPhase.Disarmed, "Cloud guard is not armed");
    public void SetScope(string value, string currentFilter) {
        if (target == value && filter == currentFilter) return;
        target = value; filter = currentFilter; activeKey = null; stars = null; solves = 0;
        if (phase != CloudPhase.RetryAllowed) clear = 0;
        guideLosses.Clear(); guideWasLost = false; lostSince = null;
        if (phase is CloudPhase.Learning or CloudPhase.Monitoring) SetPhase(CloudPhase.Learning, "Learning this target and filter");
        // Scope changes never clear an active cloud episode or extend its budget.
    }
    public void ObserveFrame(CloudFrameKey key, int detectedStars, double exposureStarted, double now) {
        Tick(now);
        if (phase is CloudPhase.Disarmed or CloudPhase.Bailed or CloudPhase.Faulted or CloudPhase.Holding ||
            key.Target != target || key.Filter != filter || exposureStarted < acceptFramesAfter ||
            detectedStars < 0 || !double.IsFinite(key.ExposureSeconds) || key.ExposureSeconds <= 0 ||
            key.BinX < 1 || key.BinY < 1 || key.Width < 1 || key.Height < 1) return;
        if (!baselines.TryGetValue(key, out var baseline)) {
            if (baselines.Count >= 128) baselines.Remove(baselines.Keys.First());
            baselines[key] = baseline = new();
        }
        activeKey = key; stars = detectedStars;
        if (baseline.Stars is null) {
            // A zero-star image is evidence, never a usable reference baseline.
            if (detectedStars == 0) {
                clear = 0;
                if (++baseline.Low >= options.BadFrames || phase == CloudPhase.RetryAllowed) Trip("Repeated zero-star light frames", now);
                return;
            }
            baseline.Low = 0;
            baseline.Initial.Add(detectedStars);
            if (baseline.Initial.Count < options.BaselineFrames) { clear = 0; return; }
            baseline.Stars = baseline.Initial.Order().ElementAt(baseline.Initial.Count / 2);
        }
        if (detectedStars < baseline.Stars * options.StarFraction) {
            clear = 0;
            if (++baseline.Low >= options.BadFrames || phase == CloudPhase.RetryAllowed)
                Trip($"Stars {detectedStars} below {options.StarFraction:P0} of reference {baseline.Stars:0}", now);
            return;
        }
        baseline.Low = 0;
        // Never learn a cloud-induced decline into the baseline.
        if (detectedStars > baseline.Stars) baseline.Stars += (detectedStars - baseline.Stars.Value) * .1;
        if (phase == CloudPhase.RetryAllowed) {
            if (detectedStars < baseline.Stars * options.RecoveryStarFraction || lostSince is not null) clear = 0;
            else if (++clear >= options.ClearFrames) {
                episodeStart = retryAt = null; guideLosses.Clear(); solves = 0;
                SetPhase(CloudPhase.Monitoring, "Clear light frames confirmed recovery");
            }
        } else SetPhase(CloudPhase.Monitoring, "Monitoring comparable light frames");
    }
    public void ObserveGuiding(bool healthy, bool intentionalPause, double now) {
        Tick(now);
        if (!options.UseGuiding || phase is CloudPhase.Disarmed or CloudPhase.Holding or CloudPhase.Bailed or CloudPhase.Faulted) return;
        if (intentionalPause) { lostSince = null; guideWasLost = false; return; }
        while (guideLosses.TryPeek(out var first) && now - first > options.FlapWindowSeconds) guideLosses.Dequeue();
        if (healthy) { lostSince = null; guideWasLost = false; return; }
        clear = 0;
        if (!guideWasLost) { guideLosses.Enqueue(now); guideWasLost = true; }
        lostSince ??= now;
        if (now - lostSince >= options.GuideLossSeconds || guideLosses.Count >= options.GuideLosses)
            Trip(guideLosses.Count >= options.GuideLosses ? "Guide stars repeatedly lost and recovered" : "Guiding lost", now);
    }
    public void ObserveSolve(bool success, double now) {
        Tick(now);
        if (phase is CloudPhase.Disarmed or CloudPhase.Holding or CloudPhase.Bailed or CloudPhase.Faulted) return;
        if (success) { solves = 0; return; }
        clear = 0;
        if (++solves >= options.SolveFailures || phase == CloudPhase.RetryAllowed) Trip("Repeated plate-solve failures", now);
    }
    public void Trip(string why, double now) {
        if (phase is CloudPhase.Disarmed or CloudPhase.Bailed or CloudPhase.Faulted) return;
        if (phase != CloudPhase.Holding) {
            episodeStart ??= now;
            retryAt = now + options.RetrySeconds;
        }
        clear = 0;
        SetPhase(CloudPhase.Holding, why);
    }
    public void Bail(string why) => SetPhase(CloudPhase.Bailed, why);
    public void Fault(string why) => SetPhase(CloudPhase.Faulted, why);
    public void BeginParkedHold(double now) {
        Tick(now);
        if (phase is not (CloudPhase.Holding or CloudPhase.RetryAllowed)) return;
        retryAt = now + options.RetrySeconds;
        SetPhase(CloudPhase.Holding, reason);
    }
    public void Tick(double now) {
        if (episodeStart is double started && now - started >= options.MaximumEpisodeSeconds && phase is CloudPhase.Holding or CloudPhase.RetryAllowed) {
            Bail("Cloud retry budget exhausted"); return;
        }
        if (phase == CloudPhase.Holding && now >= retryAt) {
            lostSince = null; guideWasLost = false; guideLosses.Clear(); solves = clear = 0;
            acceptFramesAfter = now;
            SetPhase(CloudPhase.RetryAllowed, "Retry timer elapsed — clear conditions are not yet confirmed");
        }
    }
    private void SetPhase(CloudPhase value, string why) {
        if (phase != value || reason != why) revision++;
        phase = value; reason = why;
    }
    public CloudSnapshot Snapshot(double now) {
        Tick(now);
        var baseline = activeKey is not null ? baselines.GetValueOrDefault(activeKey) : null;
        return new(phase, phase is CloudPhase.Learning or CloudPhase.Monitoring or CloudPhase.RetryAllowed, reason,
            target, filter, baseline?.Stars, stars, baseline?.Low ?? 0, solves, guideLosses.Count, clear,
            phase == CloudPhase.Holding && retryAt.HasValue ? Math.Max(0, retryAt.Value - now) : null,
            episodeStart.HasValue ? now - episodeStart.Value : null, revision);
    }
}
