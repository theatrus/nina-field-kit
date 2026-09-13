# NINA Field Kit design

Status: proposal, September 13, 2026. This document describes intended behavior, not an implemented plugin.

## Purpose

Provide a collection of useful Advanced Sequencer actions that users can combine with NINA's own instructions and plugins such as Target Scheduler and Sequencer+. Start with mount diagnostics and recovery, then add utilities where real sessions reveal a need.

Keep the individual actions small. Offer a guided recovery container for common sequences of actions. Do not make users assemble error handling from scratch just to recover a mount.

## Evidence that motivates the first actions

An ASI Mount session on September 13, 2026 showed this sequence. Times are the driver's log timestamps; their relationship to UTC requires care because the log contains inconsistent local-time offsets.

| Time | Observation |
| --- | --- |
| 05:15:54 | Tracking and pulse guiding operate near RA 20:59:41, Dec +68:00:53. |
| 05:15:56.453 | Reading SerialPort.BytesToRead throws “The port is closed.” No earlier cause appears in the supplied full log. |
| 05:15:59.364 | The driver reconnects COM7 internally; firmware reports version 1.8.8. |
| 05:16:00–05:54:42 | All 1,162 sampled states report tracking false, Dec +90 degrees, altitude 31.547 degrees, azimuth 180 degrees, pier side N, and slewing false. |
| 05:54:43.719 | Park invokes FindHome / Goto Zero. |
| 05:54:52.198 | Homing completes, then the driver parks. The operator confirms the telescope physically homed. |
| 05:54:52–07:01:16 | All 1,993 sampled states report AtHome true and Slewing true, with fixed altitude 31.547 degrees, azimuth 0 degrees, Dec +90 degrees, and tracking false. |

This establishes a transport failure, persistent suspect coordinates after reconnect, successful physical homing, and a persistent suspect slewing flag afterward. It does not establish whether USB, power, firmware, or driver code caused the initial failure. It also does not establish that all driver versions behave alike.

The full raw log stays outside this repository. It contains site and machine details. Tests should use reduced synthetic fixtures that preserve the behavior.

## Design rules

- A connection flag is not proof of working communication.
- A command returning success is not proof of physical completion.
- Tracking off is valid during waits, parking, safety handling, and some meridian-flip phases.
- After loss of position confidence, enabling tracking alone is not a full recovery.
- Use the planned target, captured before recovery, rather than copying suspect mount coordinates.
- Bound every operation and the whole recovery attempt. A timeout must stop dependent steps.
- Never silently substitute Park/Unpark for FindHome or issue a Sync from an assumed position.
- Never change or conceal a driver's reported state to make an action appear successful.
- Respect user-selected limits and NINA's safety and meridian-flip handling.

## Proposed action catalog

Names are provisional UI labels.

| Action | Behavior | Initial scope |
| --- | --- | --- |
| Mount Health Check | Read a series of mount states and evaluate selected requirements. Return healthy, unhealthy, or unknown with reasons. | First release |
| Ensure Tracking | Check tracking mode and enabled state, set the requested mode if needed, and verify the result. Never unpark or home implicitly. | First release |
| Rehome Mount | Run FindHome with a deadline and a selected completion policy. Report evidence and any unresolved state conflict. | First release |
| Recover Mount and Target | Stop acquisition and guiding, assess connection, home when required, restore tracking, reacquire the saved target, and settle guiding. | First release |
| Wait for Equipment State | Wait for a supported state with a deadline and required stable duration. | Later |
| Reconnect and Verify Equipment | Reconnect one device and perform a device-specific health check. Avoid disconnecting unrelated equipment. | Later |
| Capture Equipment Snapshot | Save a timestamped snapshot and reason to the session log or an explicitly selected diagnostic file. | First release |
| Retry with Delay | Retry suitable operations with a count and delay; reject retries that could overlap an unfinished physical action. | Later |

Do not duplicate NINA's general Connect Equipment, Set Tracking, or scripting instructions without adding verification or composition that they lack.

## Health model

Keep observations separate from conclusions. Record connection, tracking enabled/mode, slewing, AtHome, AtPark, coordinates and their epoch, pier side, read errors, read timing, and target context when available. Do not invent an age for data when a mediator only exposes cached values; distinguish a new snapshot from a confirmed device response.

Possible findings include TransportUnavailable, TrackingUnexpectedlyOff, PositionSuspect, HomeStateConflict, SlewStateConflict, and TargetUnavailable. These are internal design names, not expression syntax.

Coordinate anomalies are evidence, not universal reset detectors. Dec +90 is legitimate at home. A sudden change to +90 during guided target imaging, accompanied by a communication failure and tracking loss, is more useful than the coordinate alone. RA is degenerate at the celestial pole; an RA jump there must not be treated as an angular slew distance.

## Ensure Tracking

Inputs: desired tracking mode, verification deadline, stable duration, and policy on uncertain position. Sidereal is the normal mode for fixed deep-sky targets, not an unconditional choice for all targets.

Preconditions: active imaging intent, connected and responsive mount, unparked state, and no conflicting recovery, slew, or flip operation. If position confidence has been lost, fail with a recommendation to use full recovery.

Only change the mode or enabled state when needed. Check command results, then verify sustained reported tracking. Label this as verification of reported state, not proof that the motors move. An optional image-based check can provide stronger evidence later.

## Rehome Mount

Use the mount's supported FindHome operation. Reject unsupported hardware before moving. Inputs include a home deadline and completion policy. Do not alter the saved park position.

Default completion requires successful command completion and stable AtHome true, AtPark false or an explicitly supported parked outcome, and Slewing false. Unexpected combinations return an uncertain result and block subsequent motion.

The ASI case needs special study: AtHome stays true while Slewing stays true after physical homing. A future driver-specific policy may accept independently supported home completion despite that flag, but only for a tested driver/firmware combination and with explicit selection. It must not accept elapsed time, fixed coordinates, or AtHome alone as proof. Initial support can diagnose this case and stop for operator verification.

If NINA's FindHome call itself waits forever on Slewing, an outer timeout does not guarantee cancellation of the device operation. Before implementation, inspect the full mediator/view-model path and test cancellation. Do not issue the next command until the prior operation is known to have ended. Do not open a second serial connection to bypass NINA's driver ownership.

## Recover Mount and Target

User-facing settings: recovery level (tracking only, reconnect then assess, or rehome and reacquire), target source, tracking mode, optional autofocus, deadlines, attempt limit, and home completion policy. Default to one automatic attempt. Do not promise a one-click repair for an unvalidated mount.

1. Capture the intended target, epoch, requested rotation where available, and active plan identity. Reject a missing target for reacquisition.
2. Acquire exclusive recovery ownership. Prevent new exposures. Stop or abort the active exposure through NINA's supported lifecycle and ensure it has ended. Record any affected image as suspect without assuming a grader API exists.
3. Stop guiding. Confirm imaging intent still permits recovery and that no safety interrupt or flip owns the equipment.
4. Capture health evidence. Reconnect only if needed; a connected-but-broken driver may require an explicit, configured reconnect. Verify communication afterward.
5. If selected, execute Rehome Mount. Stop on uncertain completion. Unpark only if explicitly required by the selected workflow and allowed by current intent.
6. Restore the requested tracking mode and enabled state; verify it.
7. Recheck target visibility and meridian constraints at the current time. Slew and plate-solve/center on the saved target using NINA services. Respect the configured custom horizon and upper-altitude limit. Endpoint checks are not a model of the full physical slew path.
8. If configured, restore framing rotation and run autofocus using the user's filter/offset settings.
9. Restart guiding and wait for settle. Verify the target and mount state again.
10. Return control to the interrupted sequence only if the target plan is still valid. Let Target Scheduler choose a new plan if the saved plan has expired or changed.

On failure, stop dependent steps, retain the failure and evidence, and keep imaging suspended. Do not automatically park a mount whose position is suspect. Whether a hardware home is a suitable fallback depends on the mount and must be configured and tested.

On cancellation, distinguish a user stop or safety interrupt from a timeout. Do not restart tracking or guiding in a finally block. A failed or uncancellable hardware call blocks retries until its state is resolved.

## Triggers and imaging intent

Offer a Mount Health Guard after the manual actions work. Its condition is conceptually:

```
imaging is expected
AND recovery is not already active
AND a sustained mount-health fault is present
```

This is pseudocode, not a valid NINA or Sequencer+ expression.

Use a short configurable persistence threshold, a cooldown, a maximum attempt count, and a latch that prevents concurrent recovery. A stale Slewing flag is a health finding rather than a reason to suppress all checks forever. It is still a reason to block new physical motion until resolved.

Provide an explicit imaging-intent scope or Arm/Disarm instructions. Place them around actual acquisition, not the whole night. The guard must disarm during scheduler waits, startup/shutdown, safety handling, parking, and flip pauses. Container placement alone does not establish intent because Target Scheduler includes waits within its container.

An interrupting trigger must use NINA's supported cancellation/lifecycle path. A timer may collect observations; it must not issue competing mount commands from a background thread. Start with checks between instructions until interruption and resumption are validated.

## Target Scheduler and Sequencer+ integration

Target Scheduler owns a changing target. Never assume a generic parent container exposes the correct coordinates. Its documented coordinate injection covers specific built-in instructions; a new recovery action does not inherit that support automatically.

Define an ITargetContextProvider abstraction. Start with explicit coordinates or a verified fixed-target container. Add a Target Scheduler adapter only after validating an available API/message contract for target identity, coordinates, epoch, rotation, waiting state, and plan changes. If the required context is unavailable, allow diagnosis and homing but reject automatic reacquisition.

Center After Drift in the parent container is useful during normal imaging. It is not proof of recovery after a position reset and cannot replace explicit recovery verification.

Expose health results to NINA's UI and logs first. Expression integration with Sequencer+ or NINA's later expression system is optional and version-specific. Proposed values include tracking, transport health, position confidence, and recovery status; exact names and registration APIs remain to be designed. Do not require users to fork Sequencer+ for the basic actions.

## Implementation structure and compatibility

Use a normal NINA plugin with MEF-exported sequence items and triggers. Target NINA 3.2 first; verify the exact minimum build before publishing a manifest. This is not a Codex plugin.

- Sequence items: settings, validation, progress, persistence, and lifecycle.
- Recovery coordinator: state machine, exclusive ownership, deadlines, and cancellation.
- Device adapters: NINA mediators plus explicit capability checks.
- Health evaluator: deterministic rules over observations.
- Target context providers: fixed target first, scheduler integration separately.
- Driver policies: narrowly scoped compatibility handling backed by tests.
- Diagnostics: structured transitions, snapshots, and final result.

The inspected NINA 3.2 ITelescopeMediator exposes FindHome, ParkTelescope, UnparkTelescope, StopSlew, SetTrackingEnabled, SetTrackingMode, and coordinate slew methods. Their presence does not guarantee hardware support or completion semantics. Inspect implementations before use; some earlier slew paths were observed to discard deeper Boolean failure results.

Do not clear ASCOM flags, change driver configuration, update firmware, or reset USB hardware as an implicit repair. A reconnect policy must account for shared driver clients such as PHD2.

## Diagnostics and results

Each run gets a correlation ID, action/version, driver identity when available, start/end timestamps with explicit UTC offset, chosen policy, state transitions, command outcomes, observed states, and a final result. Include original exception details. Do not log credentials or upload files automatically.

Use distinct results: success, failure, cancelled, and uncertain. Map uncertain to a sequencer failure that prevents dependent work. Make the reason visible without requiring the user to read a stack trace. Integrate with NINA's attempt/error settings, but document that retrying the enclosing action must not overlap a prior hardware operation.

## Validation plan

Unit tests use fake device adapters and a controlled clock. Cover healthy no-op, tracking loss, cached success during a transport failure, reconnect without restored tracking, position reset, missing capabilities, home timeout, AtHome/Slewing conflict, rejected command, target change, unsafe state, meridian conflict, cancellation, shared-client reconnect, and duplicate trigger attempts.

Integration tests on Windows verify MEF discovery, settings save/load and cloning, validation, actual mediator return/cancellation behavior, trigger lifecycle, and error propagation. Do not claim hardware recovery based on mocked tests.

Hardware tests proceed under observation: tracking-only interruption, transport interruption, reconnect, home, target reacquisition, and park. Independently confirm physical home and use plate solving for final pointing. Record driver and firmware versions. Reproduce the ASI stale-slewing case and determine whether the fault is in firmware, driver interpretation, or NINA integration before enabling an automatic workaround.

## Delivery order

1. Plugin scaffold, snapshots, health checks, and verified Ensure Tracking.
2. Rehome Mount with strict completion and bounded failure behavior.
3. Recover Mount and Target with explicit target coordinates.
4. Verified Target Scheduler integration and imaging-intent handling.
5. Automatic health guard and tested driver-specific policies.
6. Additional small utilities based on actual use.

## Open questions

- Which mount/driver versions retain physical position across a serial reconnect?
- What evidence can establish home completion when Slewing is stale?
- Can FindHome cancellation end the underlying operation reliably in NINA 3.2?
- Which Target Scheduler API exposes enough context to recover and resume safely?
- Can reported-state freshness be measured without bypassing the mediator?
- How should an affected exposure be marked in Target Scheduler's grading workflow?
- Which license should the project use before distributing code?

## References

- [NINA 3.2 telescope mediator](https://github.com/isbeorn/nina/blob/release/3.2.x/NINA.Equipment/Interfaces/Mediator/ITelescopeMediator.cs)
- [NINA 3.2 telescope view model](https://github.com/isbeorn/nina/blob/release/3.2.x/NINA.WPF.Base/ViewModel/Equipment/Telescope/TelescopeVM.cs)
- [NINA 3.2 reconnect trigger](https://github.com/isbeorn/nina/blob/release/3.2.x/NINA.Sequencer/Trigger/Connect/ReconnectTrigger.cs)
- [Target Scheduler container and event instructions](https://tcpalmer.github.io/nina-scheduler/sequencer/container.html)
- [Target Scheduler sequence item notes](https://tcpalmer.github.io/nina-scheduler/sequencer/notes.html)
- [Sequencer+ documentation](https://elveteek.ch/nina-plugins/sequencer-plus)

External APIs and behavior must be rechecked against the versions selected for implementation.
