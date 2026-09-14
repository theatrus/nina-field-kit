# Implementation contracts and design review

Reviewed September 13, 2026. This document covers the mount diagnostic increment, not a tested recovery system. The separate [Alpaca safety implementation](alpaca-safety-implementation.md) adds a cached, background-refreshing safety device.

## Decisions from the review

| Gap in the proposal | Decision |
| --- | --- |
| Unspecified NINA build | Pin NINA.Plugin 3.2.0.9001 and its locked transitive dependencies; target .NET 8 Windows/WPF. |
| Meaning of healthy with cached data | Healthy means only that the selected reported-state requirements passed. It never establishes transport health, motor motion, or position confidence. |
| Tracking off during normal waits | RequireTracking is opt-in per instruction. No automatic imaging intent is inferred from a container. |
| Unknown versus failure | Preserve Unknown in evidence/UI, throw a sequencer failure, default to AbortOnError. |
| Sampling/persistence | Default three copies, one second apart; all samples must pass. This is not proof of three device polls or continuous physical stability. |
| Mutable mediator data | Copy values immediately into an immutable record. The copy is best-effort and may span an update; it is not atomic. |
| Driver errors hidden by NINA | A successful snapshot copy has no implication about upstream read success. Do not label it a new driver response. |
| Diagnostics scope | Capture Equipment Snapshot currently captures only the mount, to the NINA session log. No separate files or uploads. |
| Motion before lifecycle verification | Export only the read-only actions in this increment. No placeholder motion actions that appear usable. |
| License | Apache-2.0 for Field Kit source and plugin metadata. The repository includes the full license; third-party dependencies retain their own licenses. |

## Verified NINA source baseline

Source inspection uses the immutable [Version-3.2 commit 2393eae581145ed5b8114bf07c48ca2580540fd5](https://github.com/isbeorn/nina/tree/2393eae581145ed5b8114bf07c48ca2580540fd5). The moving release/3.2.x branch had already advanced beyond this package, so it is not the implementation baseline.

- [DeviceMediator.GetInfo](https://github.com/isbeorn/nina/blob/2393eae581145ed5b8114bf07c48ca2580540fd5/NINA.WPF.Base/Mediator/DeviceMediator.cs) returns stored device information. Copy timestamps cannot establish when hardware responded. TelescopeInfo.UTCDate is not a freshness certificate.
- [TelescopeVM](https://github.com/isbeorn/nina/blob/2393eae581145ed5b8114bf07c48ca2580540fd5/NINA.WPF.Base/ViewModel/Equipment/Telescope/TelescopeVM.cs) implements synchronous tracking setters. SetTrackingEnabled returns the resulting enabled state, so false is not generically a failed disable command. SetTrackingMode compares the read-back mode and can reset custom rates. These calls have no cancellation token.
- The same view model gives FindHome a linked ten-minute timeout, waits for the adapter and another device update, and reports a Boolean. This does not independently certify the physical operation ended.
- [AscomTelescope.FindHome](https://github.com/isbeorn/nina/blob/2393eae581145ed5b8114bf07c48ca2580540fd5/NINA.Equipment/Equipment/MyTelescope/AscomTelescope.cs) calls the external device wrapper's FindHomeAsync. Cancellation is rethrown; other exceptions are logged and swallowed. Successful return through this layer can therefore hide a device error. External wrapper and driver cancellation still need validation.
- [SequenceItem](https://github.com/isbeorn/nina/blob/2393eae581145ed5b8114bf07c48ca2580540fd5/NINA.Sequencer/SequenceItem/SequenceItem.cs) defaults to ContinueOnError. A thrown exception alone is insufficient to promise that later instructions cannot run. Our health item changes its default to AbortOnError; user changes remain under NINA's control.

These findings rule out implementing recovery by merely wrapping the built-in calls in a retry loop.

## Implemented contracts

`Nina.FieldKit.Core` has no NINA or WPF dependency. It owns observations, deterministic evaluation, and the cancellable sample window. `Nina.FieldKit.Plugin` owns MEF exports, mediator access, Newtonsoft settings persistence, WPF templates, and logging. Tests reference the plugin for host-contract checks without connecting devices.

### Capture Equipment Snapshot

One copy of cached mount state. Disconnected is valid diagnostic evidence and succeeds; an unavailable state or exception fails the instruction. Null represents unavailable fields rather than disconnected Boolean defaults. Non-finite coordinates become null. Recorded fields include UTC copy time, copy duration, source, connection, tracking mode/enabled, slewing, home/park, RA hours, declination degrees, epoch when known, and pier side. Original exception text is retained when the copy itself fails. Driver identity, actual response timestamps, and active target context are not exposed in this increment.

### Mount Health Check

| Setting | Default | Meaning |
| --- | --- | --- |
| SampleCount | 3 | Integer 1–60; one second between copies, maximum 59 scheduled seconds of waiting plus local copy/log overhead. |
| RequireTracking | false | Enable only where tracking is expected. |
| RequireUnparked | false | Require reported AtPark false. |
| RequireStationary | false | Require reported Slewing false. |
| RequireConfirmedResponse | false | When enabled, the current cached adapter returns Unknown, never a false freshness guarantee. |
| Attempts | 1 | NINA's normal instruction retry count. Read-only retries cannot start physical commands. |
| ErrorBehavior | AbortOnError | User-overridable NINA behavior; no custom interrupt or background monitor. |

Connection is always required for health evaluation. Confirmed reported violations make the window Unhealthy. Missing required evidence and conflicting home/slew or park/tracking states make it Unknown unless a definite fault already makes it Unhealthy. A later passing sample cannot erase an earlier fault. Empty windows are Unknown. Pole coordinates alone are not a fault or reset detector. Coordinates are captured for diagnosis, not validated as true pointing.

An unhealthy or unknown window throws SequenceEntityFailedException. Cancellation remains OperationCanceledException. Runtime validation repeats the sample-count bounds even if the UI validation was bypassed. Settings are frozen at execution start. Clone copies settings and NINA metadata but starts with no previous result; LastResult and Issues are not serialized.

No device calls are queued. The only mount access is GetInfo. Timing is cancellable between cached copies, and no token or timeout is advertised as cancelling a hardware operation. ObservationWindow accepts a TimeProvider for future controlled-clock scenarios.

### Logs and UI

Every run records a new correlation ID, action name, assembly version, UTC timestamp, start settings, snapshots, and final Success/Failure/Uncertain/Cancelled transition. Health UI shows the result and its evidence limitation. JSON is written through NINA's logger with enum names. Unknown does not get silently converted to healthy. Snapshots can contain pointing information; they stay in the normal local session log.

## Next implementation: verified Ensure Tracking

Before exporting the action, implement and test these contracts:

1. A shared command coordinator per telescope mediator, used by all Field Kit command actions. Ownership covers preflight through verification. A second attempt fails busy instead of waiting indefinitely. This only coordinates Field Kit; it cannot lock out other NINA plugins or manual controls.
2. An explicit intent/context provider supplying imaging permission, position confidence, and known safety/flip ownership. Missing or suspect position confidence blocks command execution; checking a box must not manufacture live transport evidence. Validate NINA lifecycle hooks before claiming cross-plugin exclusion.
3. A command adapter that records Accepted/Rejected/Faulted/Cancelled/TimedOutUnresolved separately from subsequent reported-state verification. Synchronous calls must run away from the UI thread, with at most one outstanding call. Waiting less time does not terminate a driver call.
4. On an expired wait, keep the coordinator blocked while the outstanding call is unresolved. Observe late completion/errors without issuing dependent commands. Task completion alone after a timeout does not prove physical state; require explicit reassessment before unlocking. No automatic retry, StopSlew, reconnect, unpark, or home as cleanup.
5. Validate requested mode and capability before changing anything. Initially support named non-custom tracking modes; reject Stopped and Custom for Ensure Tracking. Preserve custom-rate semantics by rejecting an unsupported transition rather than resetting rates silently.
6. Check only necessary setters, interpret each method's actual return semantics, and then verify reported mode/enabled state over a stable window. A false result or exception terminates the action. Cached state alone cannot satisfy a configured fresh-response requirement.

Proposed tunable defaults for simulator evaluation: 15-second total action budget, 3-second reported verification window, one-second samples, one attempt. These are design defaults, not implemented settings or tested driver limits.

## Recovery coordinator and target contracts

Planned transitions are CaptureTarget → AcquireOwnership → SuspendAcquisition → StopGuiding → Assess → optional Reconnect → optional Home → VerifyHome → RestoreTracking → ValidateTarget → Center → optional Rotate/Focus → SettleGuiding → Verify → ReturnControl. Each phase rechecks cancellation, intent, remaining total budget, and plan validity. Any terminal fault blocks dependent phases. A timed-out physical call enters Unresolved rather than releasing ownership in a finally block.

The target context will be an immutable value containing provider ID, plan ID/revision, RA hours, declination degrees, explicit epoch, optional rotation, and capture timestamp. Explicit coordinates are the first provider. Revalidate plan identity and visibility immediately before motion and before returning control. Do not populate this value from TelescopeInfo.TargetCoordinates without a verified ownership contract. Scheduler integration remains a separate adapter; no scheduler API was verified by this increment.

Rehome uses only the strict completion policy initially. Its gate includes capability, known command completion, stable AtHome true, supported park state, and Slewing false. The observed ASI home/slewing conflict remains unresolved and blocks motion; no firmware workaround is enabled. Full acquisition suspension, safety handling, and return-to-plan behavior require installed-host testing before automatic recovery can be exported.

## Validation and release gates

The automated suite covers evaluator faults/unknowns, legitimate tracking-off and pole states, synthetic ASI conflict, window cancellation/bounds, detached snapshot copying, original exceptions, disconnected diagnosis, settings round-trip/cloning, unknown-to-sequencer failure, zero device commands, MEF composition, and WPF resource loading on an STA thread. This is not the same as loading into NINA's complete host or exercising real hardware.

Before a diagnostic release, run the simulator checklist in the README, verify settings through NINA's actual save/load pipeline, and visually inspect templates in the host theme. Before motion, additionally validate blocked synchronous calls, late completion, rejected commands, shared coordinator behavior, lifecycle interrupts, and hardware-specific response evidence. A public distribution manifest remains outstanding.
