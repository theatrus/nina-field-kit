namespace Nina.FieldKit.Core.Safety;

public enum EndpointPhase { Unknown, FreshSafe, GraceSafe, PendingUnsafe, PendingSafe, Unsafe, Stale, Faulted }
public enum PollOutcome { Observation, TransientFailure, PermanentFailure }

public sealed record SafetyPollResult(PollOutcome Outcome, bool? IsSafe = null,
    string Reason = "", double RequestStarted = 0, double Received = 0,
    TimeSpan? RetryAfter = null, int? ErrorNumber = null, uint? ServerTransactionId = null,
    int? InterfaceVersion = null, bool? Connected = null);

public sealed record EndpointSafetySnapshot(Guid Id, string Label, EndpointPhase Phase,
    bool? RawIsSafe, bool PermitsSafe, bool RecoveryConfirmed, double? SafeAgeSeconds,
    int FailedCycles, int UnsafeReadings, int SafeReadings, double SafeHoldSeconds,
    string Reason, double? NextRequestInSeconds = null, int Attempt = 0,
    uint? ServerTransactionId = null, int? ErrorNumber = null, double? LatencySeconds = null,
    bool? UpstreamConnected = null, int? InterfaceVersion = null,
    SafetyPollResult? LastPoll = null, double? LastPollAgeSeconds = null, long CompletedPolls = 0, long FailedPolls = 0, bool CheckInProgress = false);

// Owned by the service's state lock. Time arguments are monotonic seconds, not UTC.
public sealed class EndpointSafetyState(SafetyEndpointOptions options) {
    private EndpointPhase phase = EndpointPhase.Unknown;
    private bool permits;
    private bool? raw;
    private double? lastSafeStart, safeRunStart;
    private int failures, unsafeCount, safeCount;
    private string reason = "Waiting for a valid safe observation";
    private SafetyPollResult? lastResult;

    private void Withdraw(EndpointPhase newPhase, string why) {
        permits = false;
        safeCount = 0;
        safeRunStart = null;
        phase = newPhase;
        reason = why;
    }

    private void Expire(double now) {
        if (lastSafeStart is double start && now - start >= options.MaximumSafeAgeSeconds &&
            phase is not (EndpointPhase.Stale or EndpointPhase.Unsafe or EndpointPhase.Faulted))
            Withdraw(EndpointPhase.Stale, "Last safe request exceeded its maximum age");
    }

    public void AttemptFailed(double now, string why) {
        Expire(now);
        safeCount = 0;
        safeRunStart = null;
        if (permits) phase = unsafeCount > 0 ? EndpointPhase.PendingUnsafe : EndpointPhase.GraceSafe;
        else if (phase == EndpointPhase.PendingSafe) phase = EndpointPhase.Unknown;
        reason = phase == EndpointPhase.Unsafe && unsafeCount >= options.UnsafeReadingsToUnsafe
            ? "Confirmed upstream unsafe; communication also failed: " + why : why;
    }

    public void CompleteCycle(SafetyPollResult result, double now) {
        Expire(now); // Expiry cannot be erased by a late response.
        lastResult = result;
        if (result.Outcome == PollOutcome.PermanentFailure) {
            Withdraw(EndpointPhase.Faulted, result.Reason);
            return;
        }
        if (result.Outcome == PollOutcome.TransientFailure) {
            AttemptFailed(now, result.Reason);
            failures = Math.Min(failures + 1, 1000);
            if (failures >= options.FailedCyclesToUnsafe) Withdraw(EndpointPhase.Unsafe, "Failed poll-cycle threshold reached: " + result.Reason);
            return;
        }
        if (result.IsSafe is null) {
            Withdraw(EndpointPhase.Faulted, "Observation did not contain a Boolean");
            return;
        }
        failures = 0;
        raw = result.IsSafe;
        if (raw == false) {
            safeCount = 0;
            safeRunStart = null;
            unsafeCount = Math.Min(unsafeCount + 1, 1000);
            if (unsafeCount >= options.UnsafeReadingsToUnsafe) Withdraw(EndpointPhase.Unsafe, "Upstream reports unsafe; confirmation threshold reached");
            else {
                phase = EndpointPhase.PendingUnsafe;
                reason = "Upstream reports unsafe; awaiting confirmation (prior safe age still applies)";
            }
        } else {
            unsafeCount = 0;
            if (now - result.RequestStarted >= options.MaximumSafeAgeSeconds) {
                Withdraw(EndpointPhase.Stale, "Safe response was already expired on receipt");
                return;
            }
            lastSafeStart = result.RequestStarted;
            safeRunStart ??= result.Received; // Hold begins on receipt, never before evidence arrived.
            safeCount = Math.Min(safeCount + 1, 1000);
            if (RecoveryConfirmed(now)) permits = true;
            phase = permits ? EndpointPhase.FreshSafe : EndpointPhase.PendingSafe;
            reason = permits ? "Fresh safe observation" : "Waiting for safe-reading count and return-to-safe hold";
        }
        Expire(now);
    }

    private bool RecoveryConfirmed(double now) => safeCount >= options.SafeReadingsToSafe &&
        safeRunStart is double start && now - start >= options.ReturnToSafeHoldSeconds;

    public EndpointSafetySnapshot Snapshot(double now) {
        Expire(now);
        // Only valid polls establish permission. A getter cannot finish the recovery hold.
        return new(options.Id, options.Label, phase, raw, permits, permits && RecoveryConfirmed(now),
            lastSafeStart is double last ? Math.Max(0, now - last) : null,
            failures, unsafeCount, safeCount, safeRunStart is double start ? Math.Max(0, now - start) : 0,
            reason, ServerTransactionId: lastResult?.ServerTransactionId, ErrorNumber: lastResult?.ErrorNumber,
            LatencySeconds: lastResult is null ? null : Math.Max(0, lastResult.Received - lastResult.RequestStarted),
            UpstreamConnected: lastResult?.Connected, InterfaceVersion: lastResult?.InterfaceVersion);
    }
}
