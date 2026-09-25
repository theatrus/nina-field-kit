using System.ComponentModel.Composition;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Utility;
using NINA.Sequencer.Validations;
using Nina.FieldKit.Plugin.SkyFlats;

namespace Nina.FieldKit.Plugin;

[Export(typeof(ISequenceItem))]
[ExportMetadata("Name", "Slew to sky-flat point")]
[ExportMetadata("Description", "Slew to 75° altitude opposite the Sun, then enable tracking.")]
[ExportMetadata("Icon", "TelescopeSVG")]
[ExportMetadata("Category", "NINA Field Kit")]
[JsonObject(MemberSerialization.OptIn)]
public sealed class SlewToSkyFlatPoint : SequenceItem, IValidatable {
    private readonly ITelescopeMediator telescope;
    private readonly IProfileService profiles;
    private readonly Func<double>? sunAzimuth;
    private string lastResult = "75° altitude, opposite the Sun";
    private IList<string> issues = new List<string>();

    [ImportingConstructor]
    public SlewToSkyFlatPoint(ITelescopeMediator telescope, IProfileService profiles) : this(telescope, profiles, null) { }

    public SlewToSkyFlatPoint(ITelescopeMediator telescope, IProfileService profiles, Func<double>? sunAzimuth) {
        this.telescope = telescope;
        this.profiles = profiles;
        this.sunAzimuth = sunAzimuth;
        ErrorBehavior = InstructionErrorBehavior.AbortOnError;
    }

    public string LastResult { get => lastResult; private set { lastResult = value; RaisePropertyChanged(); } }
    public IList<string> Issues { get => issues; set { issues = value; RaisePropertyChanged(); } }

    private bool ValidLocation() {
        var location = profiles.ActiveProfile.AstrometrySettings;
        return double.IsFinite(location.Latitude) && Math.Abs(location.Latitude) <= 90 &&
            double.IsFinite(location.Longitude) && Math.Abs(location.Longitude) <= 180 && double.IsFinite(location.Elevation);
    }

    public bool Validate() {
        var errors = new List<string>();
        var mount = telescope.GetInfo();
        if (!mount.Connected) errors.Add("Connect the mount before slewing.");
        else if (mount.AtPark) errors.Add("Unpark the mount before slewing.");
        if (!ValidLocation()) errors.Add("Set a valid observing location in the NINA profile.");
        Issues = errors;
        return errors.Count == 0;
    }

    public override void AfterParentChanged() => Validate();
    public override object Clone() {
        var clone = new SlewToSkyFlatPoint(telescope, profiles, sunAzimuth);
        clone.CopyMetaData(this);
        return clone;
    }

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
        var log = new RunDiagnostics(nameof(SlewToSkyFlatPoint));
        try {
            token.ThrowIfCancellationRequested();
            if (!Validate()) throw new SequenceEntityFailedException(string.Join(" ", Issues));
            var target = new CalculateNullPoint(profiles).Calculate(sunAzimuth?.Invoke()).Coordinates;
            if (!double.IsFinite(target.Azimuth.Degree) || !double.IsFinite(target.Altitude.Degree))
                throw new SequenceEntityFailedException("Could not calculate a valid sky-flat point.");
            LastResult = "Slewing to sky-flat point";
            progress?.Report(new ApplicationStatus { Status = LastResult });
            log.Write("Started", new { target = target.ToString(), nativeAltAz = telescope.GetInfo().CanSlewAltAz });
            await NullPointSlew.ExecuteAsync(telescope, target, token);
            LastResult = "Slew complete; tracking enabled";
            log.Write("Success");
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
