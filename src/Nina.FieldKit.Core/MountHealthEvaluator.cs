namespace Nina.FieldKit.Core;

public static class MountHealthEvaluator {
    public static HealthResult Evaluate(MountObservation observation, HealthRequirements requirements) {
        var findings = new List<HealthFinding>();
        void Fault(string code, string message) => findings.Add(new(code, FindingSeverity.Fault, message));
        void Unknown(string code, string message) => findings.Add(new(code, FindingSeverity.Unknown, message));
        void Require(bool? actual, bool expected, string code, string message) {
            if (actual is null) Unknown(code, "Required state unavailable");
            else if (actual != expected) Fault(code, message);
        }

        if (observation.ReadError is not null) Unknown("ObservationReadFailed", "Could not copy mount state; see snapshot for exception details");
        Require(observation.Connected, true, "TransportUnavailable", "Mount reports disconnected");
        if (observation.Connected == true) {
            if (requirements.RequireTracking) Require(observation.TrackingEnabled, true, "TrackingUnexpectedlyOff", "Tracking is required here but reports off");
            if (requirements.RequireUnparked) Require(observation.AtPark, false, "MountParked", "An unparked mount is required here");
            if (requirements.RequireStationary) Require(observation.Slewing, false, "SlewStateConflict", "Mount reports slewing");
            if (observation.AtHome == true && observation.Slewing == true)
                Unknown("HomeStateConflict", "AtHome and Slewing are both true; home completion is unresolved");
            if (observation.AtPark == true && observation.TrackingEnabled == true)
                Unknown("ParkStateConflict", "AtPark and TrackingEnabled are both true");
        }
        if (requirements.RequireConfirmedResponse && observation.Source != ObservationSource.ConfirmedDeviceResponse)
            Unknown("ResponseFreshnessUnknown", "The mediator exposes cached values without confirmed device-response freshness");

        var status = findings.Any(f => f.Severity == FindingSeverity.Fault) ? HealthStatus.Unhealthy
            : findings.Count != 0 ? HealthStatus.Unknown : HealthStatus.Healthy;
        return new(status, findings.AsReadOnly());
    }

    // Every sample must pass. A late good sample cannot erase an earlier fault.
    public static HealthResult EvaluateWindow(IReadOnlyList<MountObservation> observations, HealthRequirements requirements) {
        if (observations.Count == 0)
            return new(HealthStatus.Unknown, [new("NoObservations", FindingSeverity.Unknown, "No mount samples were collected")]);
        var findings = observations.SelectMany(o => Evaluate(o, requirements).Findings).Distinct().ToArray();
        return new(findings.Any(f => f.Severity == FindingSeverity.Fault) ? HealthStatus.Unhealthy
            : findings.Length != 0 ? HealthStatus.Unknown : HealthStatus.Healthy, findings);
    }
}
