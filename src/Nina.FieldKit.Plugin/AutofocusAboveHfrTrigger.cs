using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Autofocus;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;

namespace Nina.FieldKit.Plugin;

[Export(typeof(ISequenceTrigger))]
[ExportMetadata("Name", "AF above HFR")]
[ExportMetadata("Description", "Refocus before the next light exposure when HFR exceeds the limit.")]
[ExportMetadata("Icon", "AutoFocusAfterHFRSVG")]
[ExportMetadata("Category", "NINA Field Kit")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class AutofocusAboveHfrTrigger : SequenceTrigger, IValidatable {
    private readonly IHfrAutofocusService service;
    private readonly Func<bool> unsafeNow;
    private readonly Func<TimeSpan> autofocusDuration;
    private double? maximumHfr;
    private string lastResult = "Set maximum HFR";
    private IList<string> issues = new List<string>();

    [ImportingConstructor]
    public AutofocusAboveHfrTrigger(IProfileService profile, IImageHistoryVM history, ICameraMediator camera,
        IFilterWheelMediator filters, IFocuserMediator focuser, IAutoFocusVMFactory factory, ISafetyMonitorMediator safety)
        : this(new NinaHfrAutofocusService(profile, history, camera, filters, focuser, factory),
            () => safety.GetInfo() is { Connected: true, IsSafe: false },
            () => new RunAutofocus(profile, history, camera, filters, focuser, factory).GetEstimatedDuration()) { }

    public AutofocusAboveHfrTrigger(IHfrAutofocusService service, Func<bool>? unsafeNow = null, Func<TimeSpan>? autofocusDuration = null) {
        this.service = service;
        this.unsafeNow = unsafeNow ?? (() => false);
        this.autofocusDuration = autofocusDuration ?? (() => TimeSpan.Zero);
    }

    [JsonProperty]
    public double? MaximumHfr { get => maximumHfr; set { maximumHfr = value; RaisePropertyChanged(); LastResult = value is double limit && AutofocusAboveHfr.IsUsable(limit) ? "Waiting for HFR" : "Set maximum HFR"; Validate(); } }
    public string LastResult { get => lastResult; private set { lastResult = value; RaisePropertyChanged(); } }
    public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }

    public bool Validate() {
        var result = new List<string>(service.Validate());
        if (MaximumHfr is not double limit || !AutofocusAboveHfr.IsUsable(limit)) result.Add("Maximum HFR must be a finite number greater than zero.");
        Issues = result;
        return result.Count == 0;
    }
    public override void AfterParentChanged() => Validate();
    public override object Clone() {
        var clone = new AutofocusAboveHfrTrigger(service, unsafeNow, autofocusDuration) { MaximumHfr = MaximumHfr };
        clone.CopyMetaData(this);
        return clone;
    }
    public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
        if (nextItem is not IExposureItem { ImageType: "LIGHT" }) return false;
        if (unsafeNow()) { LastResult = "Deferred: safety monitor unsafe"; return false; }
        if (MaximumHfr is not double limit || !AutofocusAboveHfr.IsUsable(limit)) {
            LastResult = "Set maximum HFR";
            return false;
        }
        var reading = service.ReadLatest();
        if (reading == null || !AutofocusAboveHfr.IsUsable(reading.Value)) {
            LastResult = "Waiting for valid HFR";
            return false;
        }
        LastResult = $"{reading.Source}: {reading.Value:0.###} / {MaximumHfr:0.###}";
        if (reading.Value <= limit) return false;
        if (ItemUtility.IsTooCloseToMeridianFlip(Parent, autofocusDuration() + nextItem.GetEstimatedDuration())) {
            LastResult = "Deferred: meridian flip due";
            return false;
        }
        return true;
    }

    public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if (unsafeNow()) { LastResult = "Deferred: safety monitor unsafe"; return; }
        if (MaximumHfr is not double limit || !AutofocusAboveHfr.IsUsable(limit)) {
            LastResult = "Set maximum HFR";
            throw new SequenceEntityFailedException("Set a maximum HFR greater than zero before using this trigger.");
        }
        // Reuse the bounded HFR check and result validation, without exposing another sequence instruction.
        var check = new AutofocusAboveHfr(service) { MaximumHfr = limit };
        LastResult = "Running autofocus";
        try { await check.Execute(progress, token); }
        finally { LastResult = check.LastResult; }
    }
}
