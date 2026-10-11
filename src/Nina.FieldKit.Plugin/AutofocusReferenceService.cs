using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;

namespace Nina.FieldKit.Plugin;

public sealed record AutofocusReferenceScope(
    [property: JsonProperty(Required = Required.Always)] Guid ProfileId,
    [property: JsonProperty(Required = Required.Always)] string FocuserId,
    [property: JsonProperty(Required = Required.Always)] int FilterPosition,
    [property: JsonProperty(Required = Required.Always)] string FilterName);
public sealed record AutofocusReference(
    [property: JsonProperty(Required = Required.Always)] AutofocusReferenceScope Scope,
    [property: JsonProperty(Required = Required.Always)] int Position,
    [property: JsonProperty(Required = Required.Always)] double Temperature,
    [property: JsonProperty(Required = Required.Always)] DateTimeOffset CapturedAt) {
    public void Validate() {
        if (Scope is null || Scope.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(Scope.FocuserId) ||
            Scope.FilterPosition < -1 || Scope.FilterName is null ||
            (Scope.FilterPosition >= 0 && string.IsNullOrWhiteSpace(Scope.FilterName)) || Position < 0 ||
            !double.IsFinite(Temperature) || CapturedAt == default)
            throw new InvalidDataException("Saved autofocus reference is invalid. Run autofocus and save a new reference.");
    }
    public int PositionAt(double temperature, double slope) {
        Validate();
        if (!double.IsFinite(temperature) || !double.IsFinite(slope) || slope == 0)
            throw new ArgumentException("A finite temperature and non-zero steps-per-°C slope are required.");
        var position = Position + slope * (temperature - Temperature);
        if (!double.IsFinite(position) || position < 0 || position > int.MaxValue)
            throw new InvalidOperationException("Temperature-adjusted focus position is outside the supported range.");
        return checked((int)Math.Round(position, MidpointRounding.AwayFromZero));
    }
}

public sealed class AutofocusReferenceStore(IPluginOptionsAccessor options) {
    private static string Key(AutofocusReferenceScope scope) {
        // Include profile identity even though NINA also scopes plugin options by profile.
        var identity = JsonConvert.SerializeObject(scope);
        return "AutofocusReferenceV1_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
    public AutofocusReference? Load(AutofocusReferenceScope scope) {
        var json = options.GetValueString(Key(scope), "");
        if (string.IsNullOrEmpty(json)) return null;
        if (json.Length > 16384) throw new InvalidDataException("Saved autofocus reference is too large.");
        AutofocusReference reference;
        try {
            reference = JsonConvert.DeserializeObject<AutofocusReference>(json, new JsonSerializerSettings { MaxDepth = 8 })
                ?? throw new InvalidDataException("Saved autofocus reference is empty.");
        } catch (JsonException error) {
            throw new InvalidDataException("Saved autofocus reference is unreadable. Run autofocus and save a new reference.", error);
        }
        reference.Validate();
        if (reference.Scope != scope) throw new InvalidDataException("Saved autofocus reference belongs to different equipment or a different profile.");
        return reference;
    }
    public void Save(AutofocusReference reference) {
        reference.Validate();
        options.SetValueString(Key(reference.Scope), JsonConvert.SerializeObject(reference));
    }
}

public sealed record AutofocusRestoreResult(AutofocusReference? Reference, int? Position, double Temperature);
public interface IAutofocusReferenceService {
    Guid ProfileId { get; }
    DateTimeOffset UtcNow { get; }
    IList<string> Validate(bool restore);
    AutofocusReference Save(DateTimeOffset? notBefore, Guid? expectedProfile, CancellationToken token);
    Task<AutofocusRestoreResult> Restore(double slope, IProgress<ApplicationStatus>? progress, CancellationToken token);
}

public sealed class NinaAutofocusReferenceService : IAutofocusReferenceService {
    private readonly IProfileService profiles;
    private readonly IImageHistoryVM history;
    private readonly IFocuserMediator focuser;
    private readonly IFilterWheelMediator filters;
    private readonly ICameraMediator camera;
    private readonly ISafetyMonitorMediator safety;
    private readonly AutofocusReferenceStore store;
    private readonly TimeProvider clock;

    public NinaAutofocusReferenceService(IProfileService profiles, IImageHistoryVM history, IFocuserMediator focuser,
        IFilterWheelMediator filters, ICameraMediator camera, ISafetyMonitorMediator safety,
        IPluginOptionsAccessor? options = null, TimeProvider? clock = null) {
        this.profiles = profiles; this.history = history; this.focuser = focuser;
        this.filters = filters; this.camera = camera; this.safety = safety;
        store = new(options ?? new PluginOptionsAccessor(profiles, Guid.Parse(Safety.FieldKitSafetyMonitor.PluginId)));
        this.clock = clock ?? TimeProvider.System;
    }
    public Guid ProfileId => profiles.ActiveProfile.Id;
    public DateTimeOffset UtcNow => clock.GetUtcNow();
    public IList<string> Validate(bool restore) {
        var errors = new List<string>();
        try { ReadScope(); } catch (InvalidOperationException error) { errors.Add(error.Message); }
        if (restore) {
            var info = focuser.GetInfo();
            if (info?.Connected == true && !double.IsFinite(info.Temperature)) errors.Add("Focuser temperature is unavailable.");
        }
        // AF and the saved reference are checked at execution: startup validation happens before AF runs.
        return errors;
    }
    private AutofocusReferenceScope ReadScope() {
        var info = focuser.GetInfo();
        if (info?.Connected != true || string.IsNullOrWhiteSpace(info.DeviceId))
            throw new InvalidOperationException("Connect a focuser before using an autofocus reference.");
        var wheel = filters.GetInfo();
        if (wheel?.IsMoving == true) throw new InvalidOperationException("Wait for the filter wheel to stop.");
        if (wheel?.Connected == true) {
            var selected = wheel.SelectedFilter;
            if (selected is null || selected.Position < 0 || string.IsNullOrWhiteSpace(selected.Name))
                throw new InvalidOperationException("Select a named filter before using an autofocus reference.");
            return new(ProfileId, info.DeviceId, selected.Position, selected.Name);
        }
        var configured = profiles.ActiveProfile.FilterWheelSettings.Id;
        if (!string.IsNullOrEmpty(configured) && configured != "No_Device")
            throw new InvalidOperationException("Connect the configured filter wheel before using an autofocus reference.");
        return new(ProfileId, info.DeviceId, -1, string.Empty);
    }
    private void CheckScope(AutofocusReferenceScope expected) {
        if (ReadScope() != expected) throw new InvalidOperationException("Profile, focuser, or filter changed during the autofocus reference action.");
    }
    private static void CheckStationary(NINA.Equipment.Equipment.MyFocuser.FocuserInfo info) {
        if (info.IsMoving || info.IsSettling) throw new InvalidOperationException("Wait for the focuser to stop and settle.");
        if (info.TempComp) throw new InvalidOperationException("Turn off hardware temperature compensation before using an autofocus reference.");
    }
    public AutofocusReference Save(DateTimeOffset? notBefore, Guid? expectedProfile, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        var scope = ReadScope();
        if (expectedProfile is Guid id && scope.ProfileId != id)
            throw new InvalidOperationException("Profile changed since sequence initialization. Restart the sequence before saving focus.");
        var info = focuser.GetInfo();
        CheckStationary(info);
        var point = history.AutoFocusPoints?.Reverse().Select(p => p.AutoFocusPoint).Where(p => p != null)
            .OrderByDescending(p => p.Time).FirstOrDefault();
        if (point is null) throw new InvalidOperationException("Run a successful autofocus before saving its reference.");
        var captured = new DateTimeOffset(point.Time.ToUniversalTime());
        var now = UtcNow;
        if (captured < now - TimeSpan.FromMinutes(30) || captured > now + TimeSpan.FromMinutes(1) ||
            (notBefore.HasValue && captured < notBefore.Value))
            throw new InvalidOperationException("Run a fresh autofocus immediately before saving its reference.");
        if (!double.IsFinite(point.NewPosition) || point.NewPosition < 0 || point.NewPosition > int.MaxValue ||
            Math.Abs(info.Position - point.NewPosition) > 1 || !double.IsFinite(point.Temperature))
            throw new InvalidOperationException("Latest autofocus has no usable position/temperature, or focus has moved since it completed.");
        if (scope.FilterPosition >= 0 && !string.Equals(point.Filter, scope.FilterName, StringComparison.Ordinal))
            throw new InvalidOperationException("Select the autofocus result's filter before saving its reference.");
        var reference = new AutofocusReference(scope, info.Position, point.Temperature, captured);
        CheckScope(scope);
        token.ThrowIfCancellationRequested();
        store.Save(reference);
        token.ThrowIfCancellationRequested();
        CheckScope(scope);
        return reference;
    }
    public async Task<AutofocusRestoreResult> Restore(double slope, IProgress<ApplicationStatus>? progress, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if (!double.IsFinite(slope) || slope == 0) throw new ArgumentException("Set a finite, non-zero steps-per-°C slope.");
        var scope = ReadScope();
        var reference = store.Load(scope);
        CheckScope(scope);
        var info = focuser.GetInfo();
        if (reference is null) return new(null, null, info.Temperature);
        CheckStationary(info);
        if (safety.GetInfo() is { Connected: true, IsSafe: false }) throw new InvalidOperationException("Safety monitor is unsafe; focus was not restored.");
        if (camera.GetInfo()?.IsExposing == true) throw new InvalidOperationException("Wait for the camera exposure before restoring focus.");
        var temperature = info.Temperature;
        var target = reference.PositionAt(temperature, slope);
        // FocuserInfo does not expose travel limits. Use the device only to read that missing capability.
        if (focuser.GetDevice() is not IFocuser device || device.MaxStep <= 0 || target > device.MaxStep)
            throw new InvalidOperationException("Temperature-adjusted focus position is outside the focuser's travel limits.");
        new RunDiagnostics(nameof(RestoreAutofocusReference)).Write("RestorePlanned", new {
            reference, currentTemperature = temperature, slope, startingPosition = info.Position, target
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        void ProfileChanging(object? sender, EventArgs args) => stop.Cancel();
        profiles.BeforeProfileChanging += ProfileChanging;
        try {
            CheckScope(scope);
            stop.Token.ThrowIfCancellationRequested();
            progress?.Report(new ApplicationStatus { Status = $"Restoring focus to {target} steps at {temperature:0.##} °C" });
            var actual = await focuser.MoveFocuser(target, stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            CheckScope(scope);
            if (actual != target) throw new InvalidOperationException($"Focus restore requested {target} steps but returned {actual}.");
            // A predicted position is not a successful AF. The subsequent real AF resets NINA's baseline.
            return new(reference, actual, temperature);
        } finally { profiles.BeforeProfileChanging -= ProfileChanging; }
    }
}
