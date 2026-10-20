using System.ComponentModel.Composition;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Nina.FieldKit.Core;
using NINA.Image.ImageData;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;

namespace Nina.FieldKit.Plugin.SkyFlats;

public sealed record FlatRotationScope(Guid ProfileId, string CameraId, string RotatorId);
public sealed record FlatRotationPlan(FlatRotationScope Scope, DateTime Night, double[] Angles);

public interface INightRotationHistory {
    FlatRotationScope Scope { get; }
    FlatRotationPlan GetPlan(int nightOffset, double tolerance);
}

// Persist captured mechanical positions independently of Target Scheduler's database and flat-panel workflow.
[Export(typeof(NightRotationHistory))]
[Export(typeof(INightRotationHistory))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed class NightRotationHistory : INightRotationHistory, IDisposable {
    private sealed record CaptureScope(FlatRotationScope Scope, object Profile);
    private readonly object gate = new();
    private readonly IProfileService profiles;
    private readonly IImageSaveMediator images;
    private readonly IPluginOptionsAccessor options;
    private readonly TimeProvider clock;
    private readonly ConditionalWeakTable<ImageMetaData, CaptureScope> captures = new();
    private readonly RunDiagnostics log = new("SkyFlatRotations");
    private readonly Dictionary<(FlatRotationScope Scope, DateTime Night), string> recordingFaults = new();
    private bool disposed;
    private bool changingProfile;
    private DateTimeOffset acceptAfter;
    private sealed record StoredHistory(int Version, FlatRotationScope Scope, List<FlatRotationSample> Samples);

    [ImportingConstructor]
    public NightRotationHistory(IProfileService profiles, IImageSaveMediator images)
        : this(profiles, images, new PluginOptionsAccessor(profiles, Guid.Parse(Safety.FieldKitSafetyMonitor.PluginId)), TimeProvider.System) { }
    public NightRotationHistory(IProfileService profiles, IImageSaveMediator images, IPluginOptionsAccessor options, TimeProvider clock) {
        this.profiles = profiles; this.images = images; this.options = options; this.clock = clock;
        acceptAfter = clock.GetUtcNow();
        images.BeforeImageSaved += BeforeSaved;
        images.ImageSaved += Saved;
        profiles.BeforeProfileChanging += ProfileChanging; profiles.ProfileChanged += ProfileChanged;
    }
    public FlatRotationScope Scope => new(profiles.ActiveProfile.Id, profiles.ActiveProfile.CameraSettings.Id ?? "",
        profiles.ActiveProfile.RotatorSettings.Id ?? "");
    private static string Key(FlatRotationScope scope) => "NightFlatRotationsV1_" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(scope))));
    private StoredHistory Load(FlatRotationScope scope) {
        var json = options.GetValueString(Key(scope), "");
        if (string.IsNullOrEmpty(json)) return new(1, scope, new());
        try {
            if (json.Length > 524288) throw new InvalidDataException("Rotation history is too large.");
            var value = JsonConvert.DeserializeObject<StoredHistory>(json, new JsonSerializerSettings { MaxDepth = 8 });
            if (value is null || value.Version != 1 || value.Scope != scope || value.Samples is null || value.Samples.Count > 4096 ||
                value.Samples.Any(s => s is null || s.Night == default || s.Night.TimeOfDay != TimeSpan.Zero || !FlatRotations.ValidAngle(s.MechanicalDegrees)))
                throw new InvalidDataException("Rotation history is invalid or belongs to different equipment.");
            return value;
        } catch (JsonException e) { throw new InvalidDataException("Recorded flat rotations could not be read.", e); }
    }
    private Task BeforeSaved(object sender, BeforeImageSavedEventArgs args) {
        try {
            lock (gate) {
                if (!disposed && !changingProfile && args.Image?.MetaData is { Image.ImageType: "LIGHT" } meta &&
                    meta.Image.ExposureStart != DateTime.MinValue && new DateTimeOffset(meta.Image.ExposureStart.ToUniversalTime()) >= acceptAfter)
                    captures.GetValue(meta, _ => new(Scope, profiles.ActiveProfile));
            }
        } catch (Exception e) { log.Write("CaptureScopeFailed", e.GetType().Name); }
        return Task.CompletedTask;
    }
    private void ProfileChanging(object? sender, EventArgs args) { lock (gate) { changingProfile = true; captures.Clear(); } }
    private void ProfileChanged(object? sender, EventArgs args) { lock (gate) { acceptAfter = clock.GetUtcNow(); changingProfile = false; } }
    private void Saved(object? sender, ImageSavedEventArgs args) {
        FlatRotationScope? recordingScope = null; DateTime? recordingNight = null;
        try {
            var meta = args.MetaData;
            if (meta?.Image.ImageType != "LIGHT" || !FlatRotations.ValidAngle(meta.Rotator.MechanicalPosition) ||
                meta.Image.ExposureStart == DateTime.MinValue) return;
            var start = new DateTimeOffset(meta.Image.ExposureStart.ToUniversalTime());
            if (start > clock.GetUtcNow().AddSeconds(2) || start < clock.GetUtcNow().AddDays(-7)) return;
            var night = FlatRotations.ObservingNight(TimeZoneInfo.ConvertTime(start, clock.LocalTimeZone).DateTime);
            var angle = FlatRotations.Normalize(meta.Rotator.MechanicalPosition);
            lock (gate) {
                if (disposed || !captures.TryGetValue(meta, out var captured) ||
                    !ReferenceEquals(captured.Profile, profiles.ActiveProfile) || captured.Scope != Scope ||
                    string.IsNullOrWhiteSpace(captured.Scope.RotatorId) || captured.Scope.RotatorId == "No_Device") return;
                captures.Remove(meta);
                recordingScope = captured.Scope; recordingNight = night;
                var history = Load(captured.Scope);
                history.Samples.RemoveAll(s => s.Night < FlatRotations.ObservingNight(clock.GetLocalNow().DateTime).AddDays(-7));
                if (history.Samples.Any(s => s.Night == night && FlatRotations.Distance(s.MechanicalDegrees, angle) <= .01)) return;
                if (history.Samples.Count >= 4096) throw new InvalidDataException("Flat rotation history reached its limit.");
                history.Samples.Add(new(night, angle));
                options.SetValueString(Key(captured.Scope), JsonConvert.SerializeObject(history));
                log.Write("RotationRecorded", new { night, angle, captured.Scope });
            }
        } catch (Exception e) {
            lock (gate) if (recordingScope is not null && recordingNight is DateTime night)
                recordingFaults[(recordingScope, night)] = e.GetType().Name;
            log.Write("HistoryRecordFailed", e.GetType().Name);
        }
    }
    public FlatRotationPlan GetPlan(int nightOffset, double tolerance) {
        if (nightOffset is < -1 or > 0) throw new ArgumentException("Choose the current or previous observing night.");
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);
            var scope = Scope;
            var night = FlatRotations.ObservingNight(clock.GetLocalNow().DateTime).AddDays(nightOffset);
            if (recordingFaults.ContainsKey((scope, night)))
                throw new InvalidDataException("A light-frame rotation could not be recorded for this night. Review the SkyFlatRotations log before taking flats.");
            return new(scope, night, FlatRotations.Distinct(Load(scope).Samples.Where(s => s.Night == night).Select(s => s.MechanicalDegrees), tolerance));
        }
    }
    public void Dispose() {
        lock (gate) { if (disposed) return; disposed = true; captures.Clear(); }
        images.BeforeImageSaved -= BeforeSaved; images.ImageSaved -= Saved;
        profiles.BeforeProfileChanging -= ProfileChanging; profiles.ProfileChanged -= ProfileChanged;
    }
}
