using System.ComponentModel.Composition;
using System.Globalization;
using System.Windows;
using Newtonsoft.Json;
using Nina.FieldKit.Core.Clouds;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.PlateSolving;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.Utility;
using NINA.WPF.Base.Interfaces.Mediator;

namespace Nina.FieldKit.Plugin.Clouds;

public sealed record CloudTarget(string Key, string Name, Coordinates Coordinates, string Filter,
    int FilterPosition, Guid ProfileId, ISequenceContainer Context);
public interface ICloudSafetyInput {
    bool IsSafe { get; }
    string Status { get; }
    CloudSnapshot Snapshot { get; }
    event Action? Changed;
}

[Export(typeof(CloudGuardService))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class CloudGuardService : ICloudSafetyInput, IDisposable {
    private readonly object gate = new();
    private readonly IProfileService profiles;
    private readonly IImageSaveMediator images;
    private readonly IGuiderMediator guider;
    private readonly ICameraMediator camera;
    private readonly IFilterWheelMediator filters;
    private readonly ITelescopeMediator telescope;
    private readonly IImagingMediator imaging;
    private readonly IDomeMediator dome;
    private readonly IDomeFollower domeFollower;
    private readonly ISafetyMonitorMediator safety;
    private readonly IPluginOptionsAccessor settings;
    private readonly CloudDetector detector = new();
    private readonly RunDiagnostics diagnostics = new("CloudGuard");
    private readonly SemaphoreSlim recovery = new(1, 1);
    private CancellationTokenSource? run;
    private Guid? owner;
    private CloudTarget? target;
    private ISequenceRootContainer? root;
    private CloudOptions options = new();
    private CloudResponse response;
    private DateTimeOffset? morning;
    private Exception? lastSolveError;
    private bool recovering, guideExpected, exitIssued, disposed, parkRequested;
    private double? lastGuideStep;
    private long loggedRevision = -1;
    private TaskCompletionSource monitoringStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task MonitoringStarted => monitoringStarted.Task;
    private readonly TimeProvider clock;
    private readonly Func<MorningBoundary, DateTimeOffset?>? morningCalculator;
    public event Action? Changed;
    private double Now => (double)clock.GetTimestamp() / clock.TimestampFrequency;
    public CloudSnapshot Snapshot { get { lock (gate) return detector.Snapshot(Now); } }
    public bool IsSafe => Snapshot.IsSafe;
    public bool NeedsHold { get { lock (gate) return parkRequested || detector.Snapshot(Now).Phase == CloudPhase.Holding; } }
    public string Status {
        get {
            CloudSnapshot s; CloudTarget? current;
            lock (gate) { s = detector.Snapshot(Now); current = target; }
            var label = s.Phase switch { CloudPhase.Disarmed => "Not armed", CloudPhase.Holding => "Cloud hold",
                CloudPhase.RetryAllowed => "Retry allowed", CloudPhase.Bailed => "Stopped", _ => s.Phase.ToString() };
            var scope = current is null ? "" : $"{current.Name} [{(string.IsNullOrEmpty(s.Filter) ? "no filter" : s.Filter)}] | ";
            return $"{scope}{label}: {s.Reason}" + (s.Stars.HasValue ? $" | stars {s.Stars}/{(s.BaselineStars is double baseline ? baseline.ToString("0") : "learning")}" : "") +
                (s.RetryInSeconds.HasValue ? $" | retry in {s.RetryInSeconds:0} s" : "");
        }
    }
    [ImportingConstructor]
    public CloudGuardService(IProfileService profiles, IImageSaveMediator images, IGuiderMediator guider,
        ICameraMediator camera, IFilterWheelMediator filters, ITelescopeMediator telescope, IImagingMediator imaging,
        IDomeMediator dome, IDomeFollower domeFollower, ISafetyMonitorMediator safety)
        : this(profiles, images, guider, camera, filters, telescope, imaging, dome, domeFollower, safety, TimeProvider.System) { }
    public CloudGuardService(IProfileService profiles, IImageSaveMediator images, IGuiderMediator guider,
        ICameraMediator camera, IFilterWheelMediator filters, ITelescopeMediator telescope, IImagingMediator imaging,
        IDomeMediator dome, IDomeFollower domeFollower, ISafetyMonitorMediator safety, TimeProvider clock,
        IPluginOptionsAccessor? pluginOptions = null, Func<MorningBoundary, DateTimeOffset?>? morningCalculator = null) {
        this.profiles = profiles; this.images = images; this.guider = guider; this.camera = camera; this.filters = filters;
        this.telescope = telescope; this.imaging = imaging; this.dome = dome; this.domeFollower = domeFollower; this.safety = safety; this.clock = clock;
        settings = pluginOptions ?? new PluginOptionsAccessor(profiles, Guid.Parse(Safety.FieldKitSafetyMonitor.PluginId));
        this.morningCalculator = morningCalculator;
        profiles.BeforeProfileChanging += ProfileChanging;
    }
    public CloudOptions LoadOptions() {
        var json = settings.GetValueString("CloudGuardOptionsV1", "");
        try { return (string.IsNullOrEmpty(json) ? new CloudOptions() : JsonConvert.DeserializeObject<CloudOptions>(json)
            ?? throw new ArgumentException("Empty cloud settings.")).Validate(); }
        catch (JsonException e) { throw new ArgumentException("Cloud settings are invalid. Review cloud setup.", e); }
    }
    public void SaveOptions(CloudOptions value, object expectedProfile) {
        value.Validate();
        lock (gate) {
            if (!ReferenceEquals(expectedProfile, profiles.ActiveProfile)) throw new InvalidOperationException("Profile changed; reopen cloud setup.");
            settings.SetValueString("CloudGuardOptionsV1", JsonConvert.SerializeObject(value));
            options = value; detector.Configure(value);
        }
        Publish();
    }
    public object ProfileIdentity => profiles.ActiveProfile;
    public void ShowSetup() {
        void Show() => new CloudSetupWindow(this).ShowDialog();
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess()) dispatcher.Invoke(Show);
        else Show();
    }
    public IList<string> Validate(CloudResponse mode) {
        var errors = new List<string>();
        try { LoadOptions(); } catch (ArgumentException e) { errors.Add(e.Message); }
        if (!Enum.IsDefined(mode)) errors.Add("Select a cloud response mode.");
        var monitor = safety.GetDevice() as Safety.FieldKitSafetyMonitor;
        if (mode == CloudResponse.ReportUnsafe && (monitor?.Connected != true || !monitor.CloudInputEnabled))
            errors.Add("Connect Field Kit Safety Monitor and include Cloud guard in its setup for Report unsafe mode.");
        if (mode == CloudResponse.ParkAndRetry) {
            if (telescope.GetInfo()?.Connected != true) errors.Add("Connect a mount for cloud parking and retry.");
            if (camera.GetInfo()?.Connected != true) errors.Add("Connect a camera for cloud recovery plate solving.");
            if (monitor?.CloudInputEnabled == true) errors.Add("Choose Report unsafe when Field Kit's cloud safety input is enabled; only one recovery controller may own parking.");
        }
        return errors;
    }
    public void Arm(Guid id, CloudResponse mode, ISequenceRootContainer? sequence) {
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (owner.HasValue && owner != id) throw new InvalidOperationException("Use one Cloud guard trigger per running sequence.");
            if (owner == id) return;
            if (recovering) throw new InvalidOperationException("Cloud recovery is still stopping. Wait for parking cleanup before restarting the sequence.");
            if (!Enum.IsDefined(mode)) throw new ArgumentException("Select a cloud response mode.");
            options = LoadOptions();
            morning = morningCalculator is null ? CalculateMorning(options.Morning) : morningCalculator(options.Morning);
            detector.Configure(options); detector.Arm(Now);
            owner = id; response = mode; root = sequence;
            target = null; guideExpected = exitIssued = recovering = parkRequested = false; lastGuideStep = null; lastSolveError = null;
            run = new();
            images.ImageSaved += ImageSaved;
            guider.GuideEvent += GuideEvent;
            guider.GuidingStarted += GuidingResumed;
            guider.AfterDither += GuidingResumed;
            if (root is not null) root.FailureEvent += Failure;
            var lifetime = run;
            var token = lifetime.Token;
            var ready = monitoringStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(async () => { try { await WatchAsync(id, token, ready); } finally { lifetime.Dispose(); } });
        }
        Publish();
    }
    public void Disarm(Guid id) {
        lock (gate) {
            if (owner != id) return;
            owner = null;
            run?.Cancel(); run = null;
            images.ImageSaved -= ImageSaved; guider.GuideEvent -= GuideEvent;
            guider.GuidingStarted -= GuidingResumed;
            guider.AfterDither -= GuidingResumed;
            if (root is not null) root.FailureEvent -= Failure;
            root = null; detector.Disarm();
        }
        Publish();
    }
    private void ProfileChanging(object? sender, EventArgs args) { if (owner is Guid id) Disarm(id); }
    public void SetContext(ISequenceContainer? context) {
        if (context is null) return;
        IDeepSkyObjectContainer? dso = null;
        for (var node = context; node is not null; node = node.Parent)
            if (node is IDeepSkyObjectContainer found) { dso = found; break; }
        var coordinates = dso?.Target?.InputCoordinates?.Coordinates?.Transform(Epoch.J2000);
        if (coordinates is null || !double.IsFinite(coordinates.RADegrees) || !double.IsFinite(coordinates.Dec) ||
            string.IsNullOrWhiteSpace(dso!.Target.TargetName)) return;
        var selected = filters.GetInfo()?.SelectedFilter;
        var name = selected?.Name ?? "";
        lock (gate) {
            target = new(TargetKey(coordinates), dso.Target.TargetName, coordinates, name, selected?.Position ?? -1, profiles.ActiveProfile.Id, context);
            detector.SetScope(target.Key, name);
        }
    }
    public static string TargetKey(Coordinates coordinates) => string.Create(CultureInfo.InvariantCulture,
        $"{coordinates.Transform(Epoch.J2000).RADegrees:F4}/{coordinates.Transform(Epoch.J2000).Dec:F4}");
    private void ImageSaved(object? sender, ImageSavedEventArgs args) {
        try {
            var meta = args.MetaData;
            if (meta?.Image.ImageType != "LIGHT" || args.StarDetectionAnalysis is null || meta.Target.Coordinates is null ||
                meta.Image.ExposureStart == DateTime.MinValue || args.Image is null) return;
            var key = new CloudFrameKey(TargetKey(meta.Target.Coordinates), args.Filter ?? meta.FilterWheel.Filter ?? "",
                meta.Image.ExposureTime, meta.Camera.BinX, meta.Camera.BinY, meta.Camera.Gain, meta.Camera.Offset,
                args.Image.PixelWidth, args.Image.PixelHeight, meta.Camera.ReadoutModeIndex, meta.Camera.Id);
            var age = (clock.GetUtcNow() - new DateTimeOffset(meta.Image.ExposureStart.ToUniversalTime())).TotalSeconds;
            if (age < -2) return;
            lock (gate) detector.ObserveFrame(key, args.StarDetectionAnalysis.DetectedStars, Now - Math.Max(0, age), Now);
            Publish();
        } catch (Exception e) { Fail("Cloud image observation failed: " + e.GetType().Name); }
    }
    private void GuideEvent(object? sender, NINA.Core.Interfaces.IGuideStep step) {
        if (step.Event == "GuideStep" && double.IsFinite(step.RADistanceRaw) && double.IsFinite(step.DECDistanceRaw))
            lock (gate) lastGuideStep = Now;
    }
    private Task GuidingResumed(object sender, EventArgs args) {
        lock (gate) {
            if (!recovering) guideExpected = true;
            lastGuideStep = Now; detector.ObserveGuiding(true, true, Now);
        }
        return Task.CompletedTask;
    }
    private Task Failure(object sender, SequenceEntityFailureEventArgs args) {
        if (args.Entity is ISequenceItem item && IsSolve(item) && !ReferenceEquals(lastSolveError, args.Exception)) {
            lastSolveError = args.Exception;
            SetContext(item.Parent);
            lock (gate) detector.ObserveSolve(false, Now);
            Publish();
        }
        return Task.CompletedTask;
    }
    private static bool IsSolve(ISequenceItem item) => item.GetType().Namespace == "NINA.Sequencer.SequenceItem.Platesolving";
    public void AfterItem(ISequenceItem? item) {
        if (item is not null && IsSolve(item) && item.Status == SequenceEntityStatus.FINISHED) {
            lock (gate) detector.ObserveSolve(true, Now);
        }
    }
    public void CheckBoundary(ISequenceContainer? context) { SetContext(context); ObserveGuider(); Publish(); }
    private void ObserveGuider() {
        lock (gate) {
            if (recovering || owner is null || target is null) return;
            var connected = guider.GetInfo()?.Connected == true;
            var state = connected && guider.GetDevice() is IGuider device ? device.State : "Disconnected";
            if (state == "Guiding") guideExpected = true;
            if (!options.UseGuiding) return;
            if (!guideExpected) return; // Unguided imaging is supported.
            var paused = state is "Dithering" or "Settling" or "Paused" or "Calibrating" or "Looping";
            var healthy = state == "Guiding" && (lastGuideStep is null || Now - lastGuideStep <= options.GuideLossSeconds);
            detector.ObserveGuiding(healthy, paused, Now);
        }
    }
    private async Task WatchAsync(Guid runOwner, CancellationToken token, TaskCompletionSource ready) {
        try {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
            ready.TrySetResult();
            while (await timer.WaitForNextTickAsync(token)) {
                if (camera.GetInfo()?.IsExposing == true && root?.GetCurrentRunningItems().Any(i => i is IExposureItem { ImageType: "LIGHT" }) == true)
                    ObserveGuider();
                CloudTarget? current; CloudSnapshot state;
                lock (gate) {
                    if (token.IsCancellationRequested || owner != runOwner) return;
                    state = detector.Snapshot(Now); current = target;
                    if (state.EpisodeSeconds.HasValue && IsMorning) { detector.Bail("Morning boundary reached"); state = detector.Snapshot(Now); }
                }
                if (response == CloudResponse.ReportUnsafe && state.Phase is CloudPhase.Bailed or CloudPhase.Faulted && current is not null)
                    Exit(current.Context, IsMorning, runOwner, token);
                Publish();
            }
        } catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) { Fail("Cloud watcher failed: " + e.GetType().Name, runOwner, token); }
    }
    private DateTimeOffset? CalculateMorning(MorningBoundary boundary) {
        // Use NINA's astronomy routines directly; constructing a NighttimeCalculator per run leaks its periodic timer.
        var date = NighttimeCalculator.GetReferenceDate(clock.GetLocalNow().DateTime);
        var location = profiles.ActiveProfile.AstrometrySettings;
        var times = boundary switch {
            MorningBoundary.Astronomical => AstroUtil.GetNightTimes(date, location.Latitude, location.Longitude, location.Elevation),
            MorningBoundary.Nautical => AstroUtil.GetNauticalNightTimes(date, location.Latitude, location.Longitude, location.Elevation),
            MorningBoundary.Civil => AstroUtil.GetCivilNightTimes(date, location.Latitude, location.Longitude, location.Elevation),
            _ => AstroUtil.GetSunRiseAndSet(date, location.Latitude, location.Longitude, location.Elevation)
        };
        var rise = times?.Rise;
        return rise.HasValue ? new DateTimeOffset(rise.Value.ToUniversalTime()) : null;
    }
    private bool IsMorning => morning is DateTimeOffset value && clock.GetUtcNow() >= value;
    private void Publish() {
        CloudSnapshot state; bool log;
        lock (gate) {
            state = detector.Snapshot(Now);
            if (response == CloudResponse.ParkAndRetry && !recovering && state.Phase == CloudPhase.Holding) parkRequested = true;
            if (state.Phase == CloudPhase.Monitoring) parkRequested = false;
            log = loggedRevision != state.Revision;
            loggedRevision = state.Revision;
        }
        if (log) diagnostics.Write("CloudTransition", state);
        try { Changed?.Invoke(); } catch { }
    }
    private void Fail(string why, Guid? expectedOwner = null, CancellationToken lifetime = default) {
        lock (gate) {
            if (owner is null || lifetime.IsCancellationRequested || (expectedOwner.HasValue && owner != expectedOwner)) return;
            detector.Fault(why);
        }
        Publish();
    }
    public async Task HoldAsync(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
        await recovery.WaitAsync(token);
        try {
            CloudTarget? saved; CancellationToken lifetime;
            lock (gate) {
                saved = target;
                lifetime = run?.Token ?? throw new SequenceEntityFailedException("Cloud guard is not armed.");
                recovering = true;
            }
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
            var host = new RecoveryHost(this, saved, context, progress);
            await CloudHoldWorkflow.RunAsync(host, () => Snapshot, why => { lock (gate) detector.Trip(why, Now); Publish(); }, clock, stop.Token);
        } catch (Exception e) {
            if (e is not OperationCanceledException) Fail("Cloud recovery failed: " + e.GetType().Name);
            Exit(context, IsMorning);
            throw;
        } finally { lock (gate) { recovering = false; parkRequested = false; } recovery.Release(); Publish(); }
    }
    public static ISequenceContainer? FindScheduler(ISequenceContainer? context) {
        for (var node = context; node is not null; node = node.Parent)
            if (node.GetType().FullName == "NINA.Plugin.TargetScheduler.Sequencer.TargetSchedulerContainer") return node;
        return null;
    }
    public void Exit(ISequenceContainer context, bool atMorning, Guid? expectedOwner = null, CancellationToken lifetime = default) {
        lock (gate) {
            if (owner is null || exitIssued || lifetime.IsCancellationRequested || (expectedOwner.HasValue && owner != expectedOwner)) return;
            exitIssued = true;
            detector.Bail(atMorning ? "Morning boundary reached" : "Cloud hold ended; imaging container exited");
        }
        var sequenceRoot = ItemUtility.GetRootContainer(context);
        if (atMorning && sequenceRoot?.Items.Count >= 3 && sequenceRoot.Items[1] is ISequenceContainer targets) targets.Skip();
        else if (FindScheduler(context) is { } scheduler) scheduler.Skip();
        else {
            ISequenceContainer chosen = context;
            for (var node = context; node is not null; node = node.Parent)
                if (node is IDeepSkyObjectContainer) { chosen = node; break; }
            chosen.Skip();
        }
        // Skip cancels the item's token without awaiting the task currently executing this trigger.
        Publish();
    }
    private sealed class RecoveryHost(CloudGuardService service, CloudTarget? target, ISequenceContainer context,
        IProgress<ApplicationStatus> progress) : ICloudHoldHost {
        private readonly Guid profileId = service.profiles.ActiveProfile.Id;
        public bool IsMorning => service.IsMorning;
        public bool CanResume {
            get {
                if (IsMorning || target is null || service.profiles.ActiveProfile.Id != target.ProfileId) return false;
                var monitor = service.safety.GetInfo();
                if (monitor?.Connected == true && !monitor.IsSafe) return false;
                if (monitor?.Connected != true && !string.IsNullOrEmpty(service.profiles.ActiveProfile.SafetyMonitorSettings.Id) &&
                    service.profiles.ActiveProfile.SafetyMonitorSettings.Id != "No_Device") return false;
                var location = service.profiles.ActiveProfile.AstrometrySettings;
                var horizontal = target.Coordinates.Transform(Angle.ByDegree(location.Latitude), Angle.ByDegree(location.Longitude), location.Elevation);
                var dome = service.dome.GetInfo();
                if (dome?.Connected == true && dome.ShutterStatus.ToString() is not ("ShutterOpen" or "ShutterNone")) return false;
                var horizon = location.Horizon?.GetAltitude(horizontal.Azimuth.Degree) ?? 0;
                return horizontal.Altitude.Degree >= Math.Max(service.options.MinimumAltitudeDegrees, horizon);
            }
        }
        public async Task ParkAsync(CancellationToken token) {
            if (service.profiles.ActiveProfile.Id != profileId) throw new SequenceEntityFailedException("Profile changed; cloud recovery will not command the new profile's mount.");
            progress?.Report(new ApplicationStatus { Status = "Cloud event — parking and waiting" });
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(service.options.OperationTimeoutSeconds));
            try { if (service.guider.GetInfo()?.Connected == true) await service.guider.StopGuiding(limit.Token); }
            catch (Exception e) when (e is not OperationCanceledException) { service.diagnostics.Write("StopGuidingFailed", e.GetType().Name); }
            var mount = service.telescope.GetInfo();
            if (mount?.Connected != true) throw new SequenceEntityFailedException("Cannot park a disconnected mount.");
            if (!mount.AtPark && !await service.telescope.ParkTelescope(progress!, limit.Token))
                throw new SequenceEntityFailedException("Mount parking failed during a cloud event.");
            lock (service.gate) service.detector.BeginParkedHold(service.Now);
        }
        public async Task ResumeAsync(CancellationToken token) {
            var destination = target ?? throw new SequenceEntityFailedException("Cloud recovery requires a named target.");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(service.options.OperationTimeoutSeconds));
            void Check() {
                limit.Token.ThrowIfCancellationRequested();
                if (!CanResume) throw new SequenceEntityFailedException("Safety, morning, profile, or visibility prevents cloud recovery.");
            }
            Check();
            if (!await service.telescope.UnparkTelescope(progress!, limit.Token)) throw new SequenceEntityFailedException("Cloud retry could not unpark the mount.");
            Check();
            var center = new Center(service.profiles, service.telescope, service.imaging, service.filters, service.guider,
                service.dome, service.domeFollower, new PlateSolverFactoryProxy(), new NINA.Core.Utility.WindowService.WindowServiceFactory()) {
                Coordinates = new InputCoordinates { Coordinates = destination.Coordinates }, Inherited = false
            };
            await center.Execute(progress!, limit.Token);
            Check();
            if (destination.FilterPosition >= 0) {
                var filter = service.profiles.ActiveProfile.FilterWheelSettings.FilterWheelFilters
                    .FirstOrDefault(f => f.Position == destination.FilterPosition && f.Name == destination.Filter)
                    ?? throw new SequenceEntityFailedException("Imaging filter changed during the cloud hold.");
                var actual = await service.filters.ChangeFilter(filter, limit.Token, progress!);
                if (actual?.Position != destination.FilterPosition) throw new SequenceEntityFailedException("Cloud recovery could not restore the imaging filter.");
            }
            if (service.guideExpected && !await service.guider.StartGuiding(false, progress!, limit.Token))
                throw new SequenceEntityFailedException("Cloud retry could not restart guiding.");
            if (!service.guideExpected && service.guider.GetInfo()?.Connected == true) await service.guider.StopGuiding(limit.Token);
            Check();
            // Normal target/filter selection and light acquisition resume in NINA; snapshots are not enqueued as science frames.
        }
        public void ExitImaging(bool morning) => service.Exit(context, morning);
    }
    public void Dispose() {
        if (disposed) return;
        disposed = true;
        if (owner is Guid id) Disarm(id);
        profiles.BeforeProfileChanging -= ProfileChanging;
    }
}
