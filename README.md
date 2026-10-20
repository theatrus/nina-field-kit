# NINA Field Kit

![Field Kit](src/Nina.FieldKit.Plugin/Assets/field-kit.svg)

Safety monitoring, cloud recovery, focus tools and sky-flat sequencing for [NINA](https://nighttime-imaging.eu/). Field Kit combines Alpaca and local ASCOM safety sources, detects cloud events for timed recovery, enforces your autofocus HFR limit, compensates focus between light frames, saves temperature-aware autofocus references, and repeats flat instructions at the night's captured mechanical rotations.

Requires **NINA 3.2.0.9001 or newer** on Windows. Built and tested against 3.2.0.9001. Published DLLs are code-signed. Licensed under **Apache-2.0**.

## Install

1. In NINA, open **Options → General → Plugin Repositories**.
2. Add **https://nina-plugins.psf-guard.com/**.
3. Open the **Plugin Manager**, refresh Available plugins, and install **Field Kit** (shown as NINA Field Kit in versions before 0.1.0.8).
4. Restart NINA.

The source is the [theatr.us plugin registry](https://github.com/theatrus/nina-plugins-registry). Field Kit is not yet listed in NINA's default community catalog.

For manual installation, download the ZIP from [Releases](https://github.com/theatrus/nina-field-kit/releases/latest), close NINA, and extract its contents into `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Nina.FieldKit\`. Both DLLs must sit directly in that folder. Replace an earlier manual Field Kit installation in place; do not keep duplicate copies in other plugin folders. Restart NINA. Release assets include the NINA installer manifest and SHA256SUMS.txt.

## Set up the safety monitor

1. Open NINA's **Safety Monitor** equipment panel, choose **Field Kit Safety Monitor** (**Field Kit Alpaca Safety Monitor** before 0.1.0.17), and open **Setup**. Existing profile device selections keep working.
2. Choose **Add source**. Enter a name, the Alpaca server's base URL, and its SafetyMonitor device number. Do not append `/api/v1/.../issafe` to the URL. **Source type → Local ASCOM safety monitor** accepts an installed driver's ProgID instead; see the [Cloud guard guide](docs/cloud-guard.md#local-ascom-safety-sources).
3. Set **Send Alpaca Connect commands?** to **No — read status only** for a service that already reports its device as connected. Choose **Yes — send Connect when needed** if that server requires the client to connect its device before reading safety. Neither choice controls HTTP connection renewal.
4. Review **Safety behavior**. The defaults below are a starting point; set thresholds to match the source and your observing requirements.
5. Choose **Use these settings**, then **Save to profile**. Connect the monitor from NINA.

Every enabled source is required: all must satisfy their safety policy for the combined result to become safe. NINA reads the local result; its screen refreshes do not send additional server requests. Startup is unsafe until fresh readings satisfy the recovery requirements.

Sources can be added, edited, removed, or disabled while connected. **Save to profile** applies the draft live. The prior result is retained while the new configuration refreshes, bounded by the original safe-evidence expiry; repeated edits cannot extend it. New confirmed unsafe/error results can withdraw safety sooner. A separate **Test source** is available while disconnected and does not update live monitoring.

While connected, **Setup → Output override** lets you temporarily choose **Safe**, **Passthrough** (the default), or **Unsafe**. Selection takes effect immediately, without saving. Safe reports safe to NINA even when sources report unsafe or fail; Unsafe reports unsafe regardless of the sources. Passthrough restores the current combined source result. Background polling and recovery tracking continue in every mode. The status line identifies an active override and shows the actual source result; override changes also appear in the NINA log and diagnostic reports. Use this while adjusting settings, then return to Passthrough. Closing Setup or saving source settings retains the override, but disconnecting, changing profiles, suspending/resuming, or restarting clears it. It is not saved to the profile.

### Example source

For an Alpaca SafetyMonitor on your local network:

| Setting | Example |
| --- | --- |
| Name | Observatory safety |
| Server URL | `http://192.0.2.1:11111` |
| Device number | `0` |
| Send Alpaca Connect commands? | No — read status only, if the device is already connected |
| Check server every | 30 seconds |
| Missed checks tolerated | 2 |

The address above is a documentation placeholder. Replace it with your server's address and use its assigned SafetyMonitor device number. Field Kit accepts omitted success/error fields in the same way as the official ASCOM library, while still rejecting malformed readings and explicit errors.

### Checks, retries, and safety decisions

| Setting | Default | Meaning |
| --- | --- | --- |
| Check server every | 30 s | Normal delay after a completed check; requests and retries add their own duration. |
| Attempts per check | 3 | First attempt plus two retries for temporary failures. |
| Missed checks tolerated | 2 | One missed check is counted only after **all attempts fail**. The third consecutive exhausted check withdraws safety. |
| Maximum time without a safe update | 90 s | Independent age limit measured from the last successful safe request's start. Can expire before the missed-check limit. |
| Unsafe readings required | 1 | A valid unsafe response withdraws safety immediately by default; it is not retried as a communication error. |
| Safe readings required | 3 | Consecutive safe readings needed to become safe. |
| Return-to-safe hold | 10 s | Both this hold and the safe-reading count must be met by a completed safe check. |
| Request timeout | 1 s | Maximum time for each request attempt. |
| HTTP connection renewal | 1,800 s | Maximum reuse age of pooled HTTP connections. |

Like the ASCOM Alpaca client, Field Kit leaves TCP keepalive probe settings at the framework defaults; it does not explicitly enable TCP probes. Requests explicitly send `Connection: keep-alive`. HTTP connections are pooled and reused, with a 60-second idle expiry (twice the default 30-second poll interval) and a maximum reuse age of 30 minutes. A shorter configured connection lifetime also caps idle expiry. Normal polling and request timeouts determine safety freshness.

Temporary network/server errors retry with randomized, increasing delays. **Advanced → Retry tuning** controls attempts, initial delay (0.5 s), multiplier (2), and cap (30 s). Server `Retry-After` can require a longer delay. Permanent errors, including invalid response data and authentication failures, withdraw safety immediately and use slower probes.

Two tolerated misses do not promise another 90 seconds of safety after a failure: the age limit runs from the last safe request and continues during retries. A retry that succeeds avoids a missed check, but any failed attempt resets return-to-safe confirmation progress. Explicit unsafe readings use their own threshold.

The safety device reports safety to NINA. Configure and verify the desired **Advanced Sequencer response to an unsafe monitor** separately. The device alone does not command equipment; Cloud guard's optional Park and retry trigger provides its own mount recovery.

## Cloud guard

Available starting with **0.1.0.17**.

Add **NINA Field Kit → Cloud guard** to the **Triggers** of a parent imaging container. Use one trigger for the running sequence, above the target instructions or Target Scheduler container. It watches saved light-frame star counts for the current target/filter and comparable camera settings, sustained guiding loss or repeated guide-star loss, and failed native sequence plate-solve operations.

Choose one response:

- **Park and retry**: at the next light-frame boundary, stop guiding, park, wait, then unpark and recenter the saved target before imaging resumes. Recovery waits for external safety, an open dome if connected, and a visible target. Keep the safety device's Cloud guard input disabled in this mode.
- **Report unsafe**: enable **Safety Monitor Setup → Cloud guard → Include Cloud guard**, save, and connect Field Kit Safety Monitor. Your existing unsafe handler (for example, Sequencer+) owns parking and recovery. The trigger issues no mount or guiding commands in this mode. All enabled Alpaca/ASCOM sources must also permit safety.

**Setup → Allow a retry after** defaults to **300 seconds**. The cloud flag becomes safe after this timer so imaging can be tried again; the status says **Retry allowed**, since the timer does not establish clear skies. Fresh bad evidence holds again. Two clear comparable light frames confirm recovery. The default episode budget is **3600 seconds**, including trial resumptions, with a **nautical dawn** cutoff. Budget expiry skips the enclosing Target Scheduler/target container; dawn skips the root imaging area so end-of-sequence instructions can run.

Detection starts permissively while learning references. An enabled cloud safety input reports unsafe when the trigger is disarmed. This first implementation needs installed-host and equipment validation; plate-solve observation is limited to native sequencer operations. See [setup, defaults, behavior and limitations](docs/cloud-guard.md).

## Autofocus above a fixed HFR limit

Available starting with version **0.1.0.9**. Replace the older sequence instruction with this trigger in your container's **Triggers**.

1. In the **Advanced Sequencer**, add **NINA Field Kit → AF above HFR** to your imaging container's **Triggers**.
2. Enter **Maximum HFR**. New triggers start blank and require a positive limit.
3. Capture light frames normally. Before each light exposure, the trigger checks the latest image or autofocus HFR.

At or below the limit, imaging continues. Above the limit, autofocus runs before the next light exposure. It waits for valid HFR when history is missing and does not fire before calibration frames or at the end of the sequence. A connected unsafe safety monitor or an imminent meridian flip defers autofocus.

If the new autofocus fit remains above the limit or is unusable, the trigger fails through NINA's sequence failure handling. There is no internal repeat-until-good loop. The configured autofocus provider may use its normal retry settings.

**Autofocus uses the fitted HFR from its report**, not a separate post-focus verification measurement. Select **Star HFR** mode. History is session-wide, with no filter, target, binning, or age restriction; choose a limit suitable for your setup. The compact trigger shows the limit and current status. Search the NINA log for **AutofocusAboveHfr** for execution details.

Replace the old sequence instruction with this trigger when updating; saved instructions are not automatically moved into a container's Triggers. See the [trigger guide](docs/autofocus-above-hfr.md).

## Temperature compensation after a frame

Available starting with version **0.1.0.14**. In the Advanced Sequencer, add **NINA Field Kit → Temperature compensation after frame** to your imaging container's **Triggers**. Enter your measured, signed **Steps / °C** slope; it starts blank. Run autofocus before imaging to establish focus. The focuser must report temperature.

After each completed light frame, the trigger uses NINA's relative temperature-compensation method to move the focuser and wait for settling before the sequence continues. Positive slopes increase position as temperature rises; negative slopes decrease it. It adjusts position without running a full autofocus. NINA maintains the temperature baseline and fractional-step remainder, and resets the baseline after successful autofocus or manual focusing.

Unchanged temperatures skip repeated adjustments. An unsafe connected safety monitor, a moving/settling focuser, or hardware temperature compensation prevents a move. Disable hardware temperature compensation and other software temperature-compensation instructions when using this trigger. The trigger shows the last temperature and actual step adjustment; search the NINA log for **TemperatureCompensationTrigger**. Movement errors use NINA's trigger failure handling.

With **Target Scheduler**, place the Field Kit trigger in the **Triggers** of the Sequential Instruction Set surrounding Target Scheduler Container. Alternatively, NINA's existing **Move Focuser by Temp.** instruction in **Relative** mode can go in **Target Scheduler Container → Custom Event Containers → After Each Exposure**. Use one compensation mechanism. See the [temperature compensation guide](docs/temperature-compensation.md).

## Start-of-night autofocus reference

Available starting with version **0.1.0.14**. Put these instructions around your existing startup autofocus, once per night:

1. Select the filter you use for autofocus.
2. Add **NINA Field Kit → Restore autofocus reference**. Enter your measured, signed **Steps / °C** slope; it starts blank.
3. Run your normal **Run Autofocus** instruction.
4. Add **NINA Field Kit → Save autofocus reference** immediately afterward.
5. Continue your normal imaging sequence, including Target Scheduler if used.

Save records a recent successful AF's position and temperature in NINA's profile settings, separately for each focuser and filter. Overnight focus changes do not overwrite it unless the save instruction runs again. On the next night, restore moves to `saved position + slope × (current temperature − saved temperature)` and waits for settling. This brings the focuser close to the expected warm starting point before the real AF runs. A negative slope increases position when temperature falls.

With no saved reference, restore reports that and continues to the normal AF; the first night seeds the reference. Invalid references, unavailable temperature, unsafe/busy equipment, out-of-range predictions, and failed moves report errors rather than guessing a position. Disable hardware temperature compensation. Save requires the AF filter to remain selected and focus to remain at the AF result. Keep this block in nightly startup; Target Scheduler's **Before New Target** runs for each new target and would repeatedly update the reference. See the [autofocus reference guide](docs/autofocus-reference.md).

## Slew to sky-flat point

In the Advanced Sequencer, add **NINA Field Kit → Slew to sky-flat point** before your flat exposures. This is SkyFlats' null-point instruction: **75° altitude, opposite the Sun**, calculated at execution using the observing location in your NINA profile. It enables tracking after a successful slew. The mount must already be connected and unparked.

Includes the [SkyFlats mount-compatibility fix](https://github.com/photon1503/SkyFlats/pull/4): mounts without native Alt/Az slewing use NINA's RA/Dec conversion. Failed slews or failure to enable tracking fail the instruction. Cancellation is passed through to NINA. This instruction only positions the mount; it does not acquire flats or adjust exposures. No SkyFlats installation is required. See [third-party notices](THIRD-PARTY-NOTICES.md) for the imported files' MPL-2.0 license.

## Sky flats at captured rotations

Available starting with version **0.1.0.14**; use **0.1.0.16 or newer** for the corrected container editor. Add **NINA Field Kit → Sky flats at captured rotations** after your Target Scheduler imaging block, while the rotator is still connected. Expand its header, then add **SkyFlats** or other flat instructions in the normal **Instructions** section using its **+** menu or drag and drop. Children appear inside that section; nested instruction sets have their own expanders. The container moves to each distinct **mechanical** rotator position used by saved light frames, verifies arrival, and runs its children at each angle. Your child instructions own filters, exposures and the flat method.

Field Kit records light-frame rotations automatically while loaded and keeps them in profile settings across restarts, separately for each camera and rotator. **Current night** runs noon to noon in the computer's local time; choose **Previous night** when taking the preceding night's flats after noon. **Show rotations** previews the list. Close angles are grouped within **0.1°** by default; 0°/360° match, while 0°/180° remain separate.

Put a wait for morning sky brightness and any required mount positioning before the loop, or include positioning among its children. Configure the SkyFlats item for the filters and camera settings you need at each rotation. An empty night skips the loop. The container uses captured rotations recorded while Field Kit was loaded; it does not derive upcoming target angles, scan older images, or update Target Scheduler's flat history. See the [rotation-loop guide](docs/sky-flat-rotations.md).

## Twilight flats by filter and rotation (development)

The companion feature branch adds **Twilight flats by filter and rotation**: choose **Setup…** to select and order profile filters in a separate dialog. The sequence shows a compact summary, with every captured mechanical angle completed on one filter before the next. Place NINA's built-in **Twilight Sky Flats** inside its normal Instructions section and configure its exposure settings. The container supplies the filter in the order you choose; no separate SkyFlats plugin or nested rotation helper is needed. This companion is not included in **0.1.0.17**. See the [setup and execution guide](docs/twilight-filter-rotations.md).

## Status and troubleshooting

- **Setup → Monitor status** shows the combined result. The main SAFE/UNSAFE line includes source retry countdowns and attempt numbers, exhausted checks, and recovery after retry.
- **Sources** shows each draft source's polling interval and maximum safe age.
- **Live diagnostics** shows applied source state, latest result, evidence age, missed checks, and request counters. **Trace polls for 5 minutes** adds every request result and stops automatically.
- **Recent events** retains the latest 300 session events. Retries, missed checks, recovery, state changes, and lifecycle events are logged without enabling tracing.
- Search NINA's log for **FieldKitSafety**. Use **Log snapshot**, **Copy report**, or **Export diagnostics** for troubleshooting. Exports omit endpoint URLs, credentials, headers, and response bodies; source labels remain included.
- Cloud guard's trigger and Setup show the phase, reason, star count/reference and retry countdown. Search the NINA log for **CloudGuard**. Cloud-enabled diagnostic reports include the detector snapshot and target coordinates.
- **Sky flats at captured rotations → Show rotations** previews the observing date and mechanical angles. The live status shows which rotation is moving or running. Search the NINA log for **SkyFlatRotations** for recorded angles, the frozen plan and movement results.

## Build, test, and release

With the .NET 8 SDK (8.0.400 or later in the 8.0 series) on Windows:

```powershell
dotnet restore Nina.FieldKit.sln --locked-mode
dotnet build Nina.FieldKit.sln -c Release --no-restore
dotnet test Nina.FieldKit.sln -c Release --no-build
```

[CI](https://github.com/theatrus/nina-field-kit/actions/workflows/ci.yml) builds and tests on Windows, including fake-clock and loopback integration tests and rendered NINA-themed dialogs. Tests use simulated or loopback endpoints and never contact observatory hardware. NINA's transitive ToastNotifications and VVVV.FreeImage dependencies currently emit NU1701 compatibility warnings. Installed-host and sequence behavior should be validated with your equipment.

For the installed-host rotation test, close NINA and run `./tools/Run-NinaRotationAcceptance.ps1` after building Release. It launches NINA twice with a fresh simulator-only profile, verifies saved LIGHT/FLAT FITS angles and restart persistence, and restores the previous plugin DLLs afterward. Requires the ASCOM rotator simulator. See the [simulator acceptance guide](docs/nina-simulator-acceptance.md).

Release tags use four version parts, for example `v0.1.0.0`. The [signed release workflow](.github/workflows/release.yml) builds and tests the tagged source, produces code-signed DLLs, verifies their signatures, and creates the archive, SHA-256 checksum, and NINA manifest. It produces a draft GitHub release for verification. There is no unsigned release fallback.

After verifying and publishing the release, copy its manifest to `manifests/n/NINA Field Kit/3.2.0.9001/manifest.json` in [nina-plugins-registry](https://github.com/theatrus/nina-plugins-registry) and push to `main` to update the catalog. See [release notes](docs/release-notes.md).

## Fault-server testing

The development tools include a standalone Alpaca fault server for scripted errors, malformed data, latency, stalled responses, and connection failures. The integration suite runs in CI; a separate real-time suite checks production defaults and connection renewal. See [running the fault tests](docs/running-fault-tests.md) and the [.NET HTTP audit](docs/http-failure-audit.md).

## Design and license

[Cloud guard](docs/cloud-guard.md) · [Sky-flat rotation loop](docs/sky-flat-rotations.md) · [Autofocus trigger](docs/autofocus-above-hfr.md) · [Temperature compensation](docs/temperature-compensation.md) · [Nightly autofocus references](docs/autofocus-reference.md) · [Safety-monitor design](docs/alpaca-safety-monitor.md) · [Implementation notes](docs/alpaca-safety-implementation.md) · [Fault-server integration design](docs/alpaca-fault-integration-suite.md)

Copyright 2026 NINA Field Kit contributors. [Apache License, Version 2.0](LICENSE), SPDX `Apache-2.0`. Third-party dependencies retain their own licenses.
