using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Validations;

namespace Nina.FieldKit.Plugin;

[Export(typeof(ISequenceTrigger))]
[ExportMetadata("Name", "Temperature compensation after frame")]
[ExportMetadata("Description", "Adjust focus after a light frame using a measured steps-per-°C slope.")]
[ExportMetadata("Icon", "MoveFocuserByTemperatureSVG")]
[ExportMetadata("Category", "NINA Field Kit")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class TemperatureCompensationTrigger : SequenceTrigger, IValidatable, IFocuserConsumer {
    private readonly IFocuserMediator focuser;
    private readonly ISafetyMonitorMediator safety;
    private double? stepsPerDegree;
    private double? lastAppliedTemperature;
    private readonly object baselineLock = new();
    private long baselineRevision;
    private bool registered;
    private string lastResult = "Set steps per °C";
    private IList<string> issues = new List<string>();

    [ImportingConstructor]
    public TemperatureCompensationTrigger(IFocuserMediator focuser, ISafetyMonitorMediator safety) {
        this.focuser = focuser;
        this.safety = safety;
    }

    [JsonProperty]
    public double? StepsPerDegree {
        get => stepsPerDegree;
        set {
            stepsPerDegree = value;
            ResetLastApplied();
            LastResult = HasSlope ? "Waiting for light frame" : "Set steps per °C";
            RaisePropertyChanged();
        }
    }
    private bool HasSlope => StepsPerDegree is double slope && double.IsFinite(slope) && slope != 0;
    public string LastResult { get => lastResult; private set { lastResult = value; RaisePropertyChanged(); } }
    public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }

    private string? Unavailable(FocuserInfo? info) {
        if (!HasSlope) return "Set a finite, non-zero steps-per-°C slope.";
        if (info?.Connected != true) return "Connect a focuser for temperature compensation.";
        if (!double.IsFinite(info.Temperature)) return "Focuser temperature is unavailable.";
        if (info.TempComp) return "Turn off hardware temperature compensation before using this trigger.";
        return null;
    }
    public bool Validate() {
        var error = Unavailable(focuser.GetInfo());
        Issues = error is null ? new List<string>() : new List<string> { error };
        return error is null;
    }
    public override void AfterParentChanged() => Validate();
    public override void Initialize() {
        ResetLastApplied();
        LastResult = HasSlope ? "Waiting for light frame" : "Set steps per °C";
        if (!registered) {
            focuser.RegisterConsumer(this);
            registered = true;
        }
    }
    public override void Teardown() => Dispose();
    public void Dispose() {
        if (registered) {
            focuser.RemoveConsumer(this);
            registered = false;
        }
        ResetLastApplied();
    }
    private void ResetLastApplied() {
        lock (baselineLock) {
            lastAppliedTemperature = null;
            baselineRevision++;
        }
    }
    public void UpdateDeviceInfo(FocuserInfo info) {
        if (!info.Connected) ResetLastApplied();
    }
    public void UpdateEndAutoFocusRun(AutoFocusInfo info) => ResetLastApplied();
    public void UpdateUserFocused(FocuserInfo info) => ResetLastApplied();
    public void AutoFocusRunStarting() => ResetLastApplied();
    public override object Clone() {
        var clone = new TemperatureCompensationTrigger(focuser, safety) { StepsPerDegree = StepsPerDegree };
        clone.CopyMetaData(this);
        return clone;
    }
    public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) => false;
    public override bool ShouldTriggerAfter(ISequenceItem previousItem, ISequenceItem nextItem) {
        if (previousItem is not IExposureItem { ImageType: "LIGHT" } || previousItem.Status != SequenceEntityStatus.FINISHED) return false;
        if (safety.GetInfo() is { Connected: true, IsSafe: false }) { LastResult = "Deferred: safety monitor unsafe"; return false; }
        var info = focuser.GetInfo();
        if (Unavailable(info) is string reason) { ResetLastApplied(); LastResult = reason; return false; }
        if (info.IsMoving || info.IsSettling) { LastResult = "Deferred: focuser busy"; return false; }
        lock (baselineLock) {
            if (lastAppliedTemperature == info.Temperature) { LastResult = $"{info.Temperature:0.##} °C — temperature unchanged"; return false; }
        }
        return true;
    }
    public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
        var log = new RunDiagnostics(nameof(TemperatureCompensationTrigger));
        try {
            token.ThrowIfCancellationRequested();
            if (safety.GetInfo() is { Connected: true, IsSafe: false }) { LastResult = "Deferred: safety monitor unsafe"; return; }
            var info = focuser.GetInfo();
            if (Unavailable(info) is string reason) throw new SequenceEntityFailedException(reason);
            if (info.IsMoving || info.IsSettling) { LastResult = "Deferred: focuser busy"; return; }
            var temperature = info.Temperature;
            var slope = StepsPerDegree!.Value;
            var before = info.Position;
            long revision;
            lock (baselineLock) revision = baselineRevision;
            LastResult = $"Compensating at {temperature:0.##} °C";
            progress?.Report(new ApplicationStatus { Status = LastResult });
            log.Write("Started", new { temperature, slope, before });
            // NINA owns the AF/manual-focus baseline, fractional steps, limits and settling.
            var position = await focuser.MoveFocuserByTemperatureRelative(temperature, slope, token);
            token.ThrowIfCancellationRequested();
            if (position < 0) throw new SequenceEntityFailedException("Temperature compensation failed to return a focuser position.");
            lock (baselineLock) {
                // A focus event or edited slope during the move requires another check.
                if (revision == baselineRevision) lastAppliedTemperature = temperature;
            }
            LastResult = $"{temperature:0.##} °C — {(long)position - before:+0;-0;0} steps";
            log.Write("Success", new { temperature, slope, before, position });
        } catch (OperationCanceledException) {
            LastResult = "Cancelled";
            log.Write("Cancelled");
            throw;
        } catch (Exception exception) {
            LastResult = "Failed: " + exception.Message;
            log.Write("Failure", exception.ToString());
            throw;
        } finally { progress?.Report(new ApplicationStatus { Status = string.Empty }); }
    }
}
