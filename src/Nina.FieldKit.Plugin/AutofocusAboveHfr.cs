using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;

namespace Nina.FieldKit.Plugin;

public sealed record HfrReading(double Value, string Source);

public interface IHfrAutofocusService {
    HfrReading? ReadLatest();
    IList<string> Validate();
    Task<HfrReading?> RunAsync(IProgress<ApplicationStatus> progress, CancellationToken token);
}

[Export(typeof(ISequenceItem))]
[ExportMetadata("Name", "Autofocus Above HFR")]
[ExportMetadata("Description", "Run autofocus when the latest image or autofocus HFR exceeds a fixed limit. Reject a result that remains above the limit.")]
[ExportMetadata("Icon", "AutoFocusSVG")]
[ExportMetadata("Category", "NINA Field Kit")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class AutofocusAboveHfr : SequenceItem, IValidatable {
    private readonly IHfrAutofocusService service;
    private double maximumHfr = 1.7;
    private string lastResult = "Not run";
    private IList<string> issues = new List<string>();

    [ImportingConstructor]
    public AutofocusAboveHfr(IProfileService profile, IImageHistoryVM history, ICameraMediator camera,
        IFilterWheelMediator filters, IFocuserMediator focuser, IAutoFocusVMFactory factory)
        : this(new NinaHfrAutofocusService(profile, history, camera, filters, focuser, factory)) { }

    public AutofocusAboveHfr(IHfrAutofocusService service) {
        this.service = service;
        ErrorBehavior = InstructionErrorBehavior.AbortOnError;
    }

    [JsonProperty]
    public double MaximumHfr { get => maximumHfr; set { maximumHfr = value; RaisePropertyChanged(); } }
    public string LastResult { get => lastResult; private set { lastResult = value; RaisePropertyChanged(); } }
    public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }
    public static bool IsUsable(double value) => double.IsFinite(value) && value > 0;

    public bool Validate() {
        var result = new List<string>(service.Validate());
        if (!IsUsable(MaximumHfr)) result.Add("Maximum HFR must be a finite number greater than zero.");
        Issues = result;
        return result.Count == 0;
    }
    public override void AfterParentChanged() => Validate();
    public override object Clone() {
        var clone = new AutofocusAboveHfr(service) { MaximumHfr = MaximumHfr };
        clone.CopyMetaData(this);
        return clone;
    }

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
        var limit = MaximumHfr;
        var log = new RunDiagnostics(nameof(AutofocusAboveHfr));
        try {
            token.ThrowIfCancellationRequested();
            if (!IsUsable(limit)) throw new SequenceEntityFailedException("Maximum HFR must be a finite number greater than zero.");
            var reading = service.ReadLatest();
            log.Write("Evaluated", new { limit, reading });
            if (reading != null && IsUsable(reading.Value) && reading.Value <= limit) {
                LastResult = $"{reading.Source} HFR {reading.Value:0.###} ≤ {limit:0.###}: autofocus skipped";
                log.Write("Skipped", LastResult);
                return;
            }
            var errors = service.Validate();
            if (errors.Count > 0) throw new SequenceEntityFailedException(string.Join(" ", errors));
            LastResult = reading != null && IsUsable(reading.Value)
                ? $"{reading.Source} HFR {reading.Value:0.###} > {limit:0.###}: running autofocus"
                : "Latest HFR unavailable: running autofocus";
            log.Write("AutofocusStarted", LastResult);
            progress?.Report(new ApplicationStatus { Status = LastResult });
            var result = await service.RunAsync(progress!, token);
            token.ThrowIfCancellationRequested();
            if (result == null || !IsUsable(result.Value))
                throw new SequenceEntityFailedException("Autofocus did not return a usable HFR result.");
            if (result.Value > limit)
                throw new SequenceEntityFailedException($"Autofocus HFR {result.Value:0.###} exceeds maximum {limit:0.###}.");
            LastResult = $"Autofocus HFR {result.Value:0.###} ≤ {limit:0.###}: accepted";
            log.Write("Accepted", new { limit, result });
        } catch (OperationCanceledException) {
            LastResult = "Cancelled";
            log.Write("Cancelled");
            throw;
        } catch (Exception exception) {
            LastResult = "Failed: " + exception.Message;
            log.Write("Failure", exception.ToString());
            throw;
        } finally {
            progress?.Report(new ApplicationStatus { Status = string.Empty });
        }
    }
}
