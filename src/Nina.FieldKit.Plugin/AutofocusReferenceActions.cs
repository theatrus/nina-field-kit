using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;
using NINA.WPF.Base.Interfaces.ViewModel;

namespace Nina.FieldKit.Plugin;

public abstract class AutofocusReferenceAction : SequenceItem, IValidatable {
    protected readonly IAutofocusReferenceService service;
    private string lastResult = "Not run";
    private IList<string> issues = new List<string>();
    protected AutofocusReferenceAction(IAutofocusReferenceService service) {
        this.service = service;
        ErrorBehavior = InstructionErrorBehavior.AbortOnError;
    }
    public string LastResult { get => lastResult; protected set { lastResult = value; RaisePropertyChanged(); } }
    public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }
    protected abstract bool IsRestore { get; }
    protected virtual IEnumerable<string> SettingsErrors() => Array.Empty<string>();
    public bool Validate() {
        Issues = service.Validate(IsRestore).Concat(SettingsErrors()).ToList();
        return Issues.Count == 0;
    }
    public override void AfterParentChanged() => Validate();
    private protected async Task RunLogged(Func<RunDiagnostics, Task> execute, IProgress<ApplicationStatus>? progress, CancellationToken token) {
        var log = new RunDiagnostics(GetType().Name);
        try {
            token.ThrowIfCancellationRequested();
            if (!Validate()) throw new SequenceEntityFailedException(string.Join(" ", Issues));
            await execute(log);
        } catch (OperationCanceledException) {
            LastResult = "Cancelled";
            log.Write("Cancelled");
            throw;
        } catch (Exception error) {
            LastResult = "Failed: " + error.Message;
            log.Write("Failure", error.ToString());
            throw;
        } finally { progress?.Report(new ApplicationStatus { Status = string.Empty }); }
    }
}

[Export(typeof(ISequenceItem))]
[ExportMetadata("Name", "Save autofocus reference")]
[ExportMetadata("Description", "Save the latest successful autofocus position and temperature for another night.")]
[ExportMetadata("Icon", "AutoFocusSVG")]
[ExportMetadata("Category", "NINA Field Kit")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class SaveAutofocusReference : AutofocusReferenceAction {
    private DateTimeOffset? notBefore;
    private Guid? expectedProfile;
    [ImportingConstructor]
    public SaveAutofocusReference(IProfileService profiles, IImageHistoryVM history, IFocuserMediator focuser,
        IFilterWheelMediator filters, ICameraMediator camera, ISafetyMonitorMediator safety)
        : this(new NinaAutofocusReferenceService(profiles, history, focuser, filters, camera, safety)) { }
    public SaveAutofocusReference(IAutofocusReferenceService service) : base(service) { }
    protected override bool IsRestore => false;
    public override void Initialize() {
        notBefore = service.UtcNow;
        expectedProfile = service.ProfileId;
        LastResult = "Waiting for startup autofocus";
    }
    public override object Clone() {
        var clone = new SaveAutofocusReference(service);
        clone.CopyMetaData(this);
        return clone;
    }
    public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) => RunLogged(log => {
        var reference = service.Save(notBefore, expectedProfile, token);
        LastResult = $"Saved {reference.Position} steps at {reference.Temperature:0.##} °C";
        log.Write("Saved", reference);
        return Task.CompletedTask;
    }, progress, token);
}

[Export(typeof(ISequenceItem))]
[ExportMetadata("Name", "Restore autofocus reference")]
[ExportMetadata("Description", "Restore a temperature-adjusted starting position before running autofocus.")]
[ExportMetadata("Icon", "MoveFocuserByTemperatureSVG")]
[ExportMetadata("Category", "NINA Field Kit")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class RestoreAutofocusReference : AutofocusReferenceAction {
    private double? stepsPerDegree;
    [ImportingConstructor]
    public RestoreAutofocusReference(IProfileService profiles, IImageHistoryVM history, IFocuserMediator focuser,
        IFilterWheelMediator filters, ICameraMediator camera, ISafetyMonitorMediator safety)
        : this(new NinaAutofocusReferenceService(profiles, history, focuser, filters, camera, safety)) { }
    public RestoreAutofocusReference(IAutofocusReferenceService service) : base(service) { LastResult = "Set steps per °C"; }
    protected override bool IsRestore => true;
    [JsonProperty]
    public double? StepsPerDegree {
        get => stepsPerDegree;
        set {
            stepsPerDegree = value;
            LastResult = HasSlope ? "Ready to restore before autofocus" : "Set steps per °C";
            RaisePropertyChanged();
        }
    }
    private bool HasSlope => StepsPerDegree is double slope && double.IsFinite(slope) && slope != 0;
    protected override IEnumerable<string> SettingsErrors() => HasSlope ? Array.Empty<string>() : new[] { "Set a finite, non-zero steps-per-°C slope." };
    public override object Clone() {
        var clone = new RestoreAutofocusReference(service) { StepsPerDegree = StepsPerDegree };
        clone.CopyMetaData(this);
        return clone;
    }
    public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) => RunLogged(async log => {
        var slope = StepsPerDegree!.Value;
        log.Write("Started", new { slope });
        var result = await service.Restore(slope, progress, token);
        token.ThrowIfCancellationRequested();
        if (result.Reference is null) {
            LastResult = "No saved reference — run autofocus";
            log.Write("Skipped", new { reason = "No reference for this profile, focuser and filter" });
        } else {
            LastResult = $"Restored {result.Position} steps at {result.Temperature:0.##} °C";
            log.Write("Restored", new { slope, result });
        }
    }, progress, token);
}
