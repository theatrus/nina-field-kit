using System.ComponentModel.Composition;
using Newtonsoft.Json;
using Nina.FieldKit.Core;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;

namespace Nina.FieldKit.Plugin;

[Export(typeof(ISequenceItem))]
[ExportMetadata("Name", "Capture Equipment Snapshot")]
[ExportMetadata("Description", "Log a mount snapshot from NINA's cached state. Does not command equipment.")]
[ExportMetadata("Icon", "TelescopeSVG")]
[ExportMetadata("Category", "NINA Field Kit")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class CaptureEquipmentSnapshot : SequenceItem {
    private readonly ITelescopeMediator telescope;
    private string lastResult = "Not run";
    public string LastResult { get => lastResult; private set { lastResult = value; RaisePropertyChanged(); } }

    [ImportingConstructor]
    public CaptureEquipmentSnapshot(ITelescopeMediator telescope) => this.telescope = telescope;

    public override object Clone() {
        var clone = new CaptureEquipmentSnapshot(telescope);
        clone.CopyMetaData(this);
        return clone;
    }

    public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
        var run = new RunDiagnostics(nameof(CaptureEquipmentSnapshot));
        run.Write("Started");
        try {
            token.ThrowIfCancellationRequested();
            var snapshot = new MountSnapshotReader(telescope).Capture();
            run.Write("Snapshot", snapshot);
            token.ThrowIfCancellationRequested();
            if (snapshot.ReadError is not null) {
                LastResult = "Snapshot unavailable; see NINA log for details.";
                throw new SequenceEntityFailedException(LastResult);
            }
            LastResult = "Mount snapshot logged. " + HealthResult.EvidenceLimit;
            run.Write("Success", LastResult);
            return Task.CompletedTask;
        } catch (OperationCanceledException) {
            LastResult = "Cancelled";
            run.Write("Cancelled");
            throw;
        } catch (Exception exception) {
            LastResult = "Failure: " + exception.Message;
            run.Write("Failure", exception.ToString());
            throw;
        }
    }
}
