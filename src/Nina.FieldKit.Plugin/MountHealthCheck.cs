using System.ComponentModel.Composition;
using Newtonsoft.Json;
using Nina.FieldKit.Core;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;

namespace Nina.FieldKit.Plugin;

[Export(typeof(ISequenceItem))]
[ExportMetadata("Name", "Mount Health Check")]
[ExportMetadata("Description", "Check selected reported mount states across a bounded sample window. Does not command equipment.")]
[ExportMetadata("Icon", "TelescopeSVG")]
[ExportMetadata("Category", "NINA Field Kit")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class MountHealthCheck : SequenceItem, IValidatable {
    private readonly ITelescopeMediator telescope;
    private int sampleCount = 3;
    private bool requireTracking, requireUnparked, requireStationary, requireConfirmedResponse;
    private string lastResult = "Not run";
    private IList<string> issues = new List<string>();

    [ImportingConstructor]
    public MountHealthCheck(ITelescopeMediator telescope) {
        this.telescope = telescope;
        ErrorBehavior = InstructionErrorBehavior.AbortOnError;
    }

    [JsonProperty] public int SampleCount { get => sampleCount; set { sampleCount = value; RaisePropertyChanged(); } }
    [JsonProperty] public bool RequireTracking { get => requireTracking; set { requireTracking = value; RaisePropertyChanged(); } }
    [JsonProperty] public bool RequireUnparked { get => requireUnparked; set { requireUnparked = value; RaisePropertyChanged(); } }
    [JsonProperty] public bool RequireStationary { get => requireStationary; set { requireStationary = value; RaisePropertyChanged(); } }
    [JsonProperty] public bool RequireConfirmedResponse { get => requireConfirmedResponse; set { requireConfirmedResponse = value; RaisePropertyChanged(); } }
    public string LastResult { get => lastResult; private set { lastResult = value; RaisePropertyChanged(); } }
    public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }

    public bool Validate() {
        Issues = SampleCount is < 1 or > 60 ? new List<string> { "Sample count must be between 1 and 60." } : new List<string>();
        return Issues.Count == 0;
    }

    public override void AfterParentChanged() => Validate();

    public override object Clone() {
        var clone = new MountHealthCheck(telescope) {
            SampleCount = SampleCount, RequireTracking = RequireTracking, RequireUnparked = RequireUnparked,
            RequireStationary = RequireStationary, RequireConfirmedResponse = RequireConfirmedResponse
        };
        clone.CopyMetaData(this);
        return clone;
    }

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
        var run = new RunDiagnostics(nameof(MountHealthCheck));
        // Freeze settings so UI edits cannot change the requirements midway through a run.
        var count = SampleCount;
        var requirements = new HealthRequirements(RequireTracking, RequireUnparked, RequireStationary, RequireConfirmedResponse);
        run.Write("Started", new { sampleCount = count, requirements });
        LastResult = "Checking reported mount state";
        var finalWritten = false;
        try {
            token.ThrowIfCancellationRequested();
            if (count is < 1 or > 60) throw new SequenceEntityFailedException("Sample count must be between 1 and 60.");
            progress?.Report(new ApplicationStatus { Status = LastResult });
            var reader = new MountSnapshotReader(telescope);
            var samples = await ObservationWindow.CaptureAsync(() => {
                var snapshot = reader.Capture();
                run.Write("Snapshot", snapshot);
                return snapshot;
            }, count, token);
            var result = MountHealthEvaluator.EvaluateWindow(samples, requirements);
            LastResult = result.Status + ": " + result.Summary;
            run.Write(result.Status == HealthStatus.Healthy ? "Success" : result.Status == HealthStatus.Unknown ? "Uncertain" : "Failure", result);
            finalWritten = true;
            if (result.Status != HealthStatus.Healthy) throw new SequenceEntityFailedException(LastResult);
        } catch (OperationCanceledException) {
            LastResult = "Cancelled";
            run.Write("Cancelled");
            throw;
        } catch (Exception exception) {
            if (!finalWritten) {
                LastResult = "Failure: " + exception.Message;
                run.Write("Failure", exception.ToString());
            }
            throw;
        } finally {
            progress?.Report(new ApplicationStatus { Status = string.Empty });
        }
    }
}
