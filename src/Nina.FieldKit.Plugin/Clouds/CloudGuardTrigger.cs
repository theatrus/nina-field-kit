using System.ComponentModel.Composition;
using System.Windows.Input;
using Newtonsoft.Json;
using Nina.FieldKit.Core.Clouds;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;

namespace Nina.FieldKit.Plugin.Clouds;

[Export(typeof(ISequenceTrigger))]
[ExportMetadata("Name", "Cloud guard")]
[ExportMetadata("Description", "Watch stars, guiding and solve failures; park and retry or report unsafe.")]
[ExportMetadata("Icon", "SafetyMonitorSVG")]
[ExportMetadata("Category", "NINA Field Kit")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class CloudGuardTrigger : SequenceTrigger, IValidatable {
    private readonly CloudGuardService service;
    private readonly Guid id = Guid.NewGuid();
    private bool armed;
    private CloudResponse response;
    private IList<string> issues = new List<string>();
    [ImportingConstructor]
    public CloudGuardTrigger(CloudGuardService service) => this.service = service;
    [JsonProperty]
    public CloudResponse Response { get => response; set {
        if (armed && value != response) throw new InvalidOperationException("Restart the sequence to change the cloud recovery owner.");
        response = value; RaisePropertyChanged();
    } }
    public bool CanChangeResponse => !armed;
    public IReadOnlyList<CloudResponse> Responses { get; } = Enum.GetValues<CloudResponse>();
    public string LastResult => service.Status;
    public ICommand SetupCommand => new GalaSoft.MvvmLight.Command.RelayCommand(service.ShowSetup);
    public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }
    public bool Validate() { Issues = service.Validate(Response); return Issues.Count == 0; }
    public override void AfterParentChanged() => Validate();
    public override object Clone() {
        var clone = new CloudGuardTrigger(service) { Response = Response };
        clone.CopyMetaData(this); return clone;
    }
    public override void Initialize() {
        if (Status == SequenceEntityStatus.DISABLED || armed) return;
        service.Arm(id, Response, ItemUtility.GetRootContainer(Parent));
        armed = true; service.Changed += Changed; RaisePropertyChanged(nameof(CanChangeResponse));
    }
    private void Changed() => RaisePropertyChanged(nameof(LastResult));
    public override void Teardown() {
        service.Changed -= Changed;
        if (armed) service.Disarm(id);
        armed = false;
        RaisePropertyChanged(nameof(CanChangeResponse));
    }
    public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
        if (!armed || nextItem is not IExposureItem { ImageType: "LIGHT" }) return false;
        service.CheckBoundary(nextItem.Parent ?? Parent);
        var state = service.Snapshot;
        if (state.Phase is CloudPhase.Bailed or CloudPhase.Faulted) {
            if (Response == CloudResponse.ParkAndRetry) return true;
            service.Exit(nextItem.Parent ?? Parent, false); return false;
        }
        return Response == CloudResponse.ParkAndRetry && service.NeedsHold;
    }
    public override bool ShouldTriggerAfter(ISequenceItem previousItem, ISequenceItem nextItem) {
        if (armed) service.AfterItem(previousItem);
        return false;
    }
    public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) =>
        Response == CloudResponse.ReportUnsafe ? Task.CompletedTask : service.HoldAsync(context, progress, token);
}
