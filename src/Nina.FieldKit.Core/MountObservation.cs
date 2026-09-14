namespace Nina.FieldKit.Core;

public enum ObservationSource { CachedMediator, ConfirmedDeviceResponse }
public enum HealthStatus { Healthy, Unhealthy, Unknown }
public enum FindingSeverity { Fault, Unknown }

// Null means unavailable, including disconnected fields whose defaults are not evidence.
// CapturedAt is the time of the copy, never a device-response timestamp.
public sealed record MountObservation(
    DateTimeOffset CapturedAt,
    TimeSpan ReadDuration,
    ObservationSource Source,
    bool? Connected = null,
    bool? TrackingEnabled = null,
    string? TrackingMode = null,
    bool? Slewing = null,
    bool? AtHome = null,
    bool? AtPark = null,
    double? RightAscensionHours = null,
    double? DeclinationDegrees = null,
    string? Epoch = null,
    string? PierSide = null,
    string? ReadError = null);

public sealed record HealthRequirements(
    bool RequireTracking = false,
    bool RequireUnparked = false,
    bool RequireStationary = false,
    bool RequireConfirmedResponse = false);

public sealed record HealthFinding(string Code, FindingSeverity Severity, string Message);

public sealed record HealthResult(HealthStatus Status, IReadOnlyList<HealthFinding> Findings) {
    public const string EvidenceLimit = "Reported state only; cached snapshots do not establish live communication or physical position.";
    public string Summary => Findings.Count == 0
        ? "Selected reported-state requirements passed. " + EvidenceLimit
        : string.Join("; ", Findings.Select(f => f.Code + ": " + f.Message)) + ". " + EvidenceLimit;
}
