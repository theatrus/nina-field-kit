# NINA Field Kit

![Field Kit](src/Nina.FieldKit.Plugin/Assets/field-kit.svg)

Alpaca safety monitoring and an absolute HFR autofocus check for [NINA](https://nighttime-imaging.eu/). Field Kit combines required safety sources into one safe/unsafe result and adds a sequence action that rejects autofocus results above your chosen HFR limit.

Requires **NINA 3.2.0.9001 or newer** on Windows. Built and tested against 3.2.0.9001. Published DLLs are code-signed. Licensed under **Apache-2.0**.

## Install

1. In NINA, open **Options → General → Plugin Repositories**.
2. Add **https://nina-plugins.psf-guard.com/**.
3. Open the **Plugin Manager**, refresh Available plugins, and install **NINA Field Kit**.
4. Restart NINA.

The source is the [theatr.us plugin registry](https://github.com/theatrus/nina-plugins-registry). Field Kit is not yet listed in NINA's default community catalog.

For manual installation, download the ZIP from [Releases](https://github.com/theatrus/nina-field-kit/releases/latest), close NINA, and extract its contents into `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Nina.FieldKit\`. Both DLLs must sit directly in that folder. Replace an earlier manual Field Kit installation in place; do not keep duplicate copies in other plugin folders. Restart NINA. Release assets include the NINA installer manifest and SHA256SUMS.txt.

## Set up the safety monitor

1. Open NINA's **Safety Monitor** equipment panel, choose **Field Kit Alpaca Safety Monitor**, and open **Setup**.
2. Choose **Add source**. Enter a name, the Alpaca server's base URL, and its SafetyMonitor device number. Do not append `/api/v1/.../issafe` to the URL.
3. Set **Send Alpaca Connect commands?** to **No — read status only** for a service that already reports its device as connected. Choose **Yes — send Connect when needed** if that server requires the client to connect its device before reading safety. Neither choice controls HTTP connection renewal.
4. Review **Safety behavior**. The defaults below are a starting point; set thresholds to match the source and your observing requirements.
5. Choose **Use these settings**, then **Save to profile**. Connect the monitor from NINA.

Every enabled source is required: all must satisfy their safety policy for the combined result to become safe. NINA reads the local result; its screen refreshes do not send additional server requests. Startup is unsafe until fresh readings satisfy the recovery requirements.

Sources can be added, edited, removed, or disabled while connected. **Save to profile** applies the draft live. The prior result is retained while the new configuration refreshes, bounded by the original safe-evidence expiry; repeated edits cannot extend it. New confirmed unsafe/error results can withdraw safety sooner. A separate **Test source** is available while disconnected and does not update live monitoring.

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

Temporary network/server errors retry with randomized, increasing delays. **Advanced → Retry tuning** controls attempts, initial delay (0.5 s), multiplier (2), and cap (30 s). Server `Retry-After` can require a longer delay. Permanent errors, including invalid response data and authentication failures, withdraw safety immediately and use slower probes.

Two tolerated misses do not promise another 90 seconds of safety after a failure: the age limit runs from the last safe request and continues during retries. A retry that succeeds avoids a missed check, but any failed attempt resets return-to-safe confirmation progress. Explicit unsafe readings use their own threshold.

The monitor reports safety to NINA. Configure and verify the desired **Advanced Sequencer response to an unsafe monitor** separately; Field Kit does not park a mount, close a roof, or stop acquisition itself.

## Autofocus above a fixed HFR limit

Available in **0.1.0.6 and newer**.

1. Open the **Advanced Sequencer** and add **NINA Field Kit → Autofocus Above HFR** after an image or an autofocus instruction.
2. Set **Maximum HFR** to your acceptable limit (default **1.7**).
3. Put the action inside the imaging loop to check each iteration. It runs only when the sequence reaches it.

| Latest HFR with a limit of 1.7 | Action |
| --- | --- |
| 1.6 or 1.7 | Skip autofocus. |
| 3.0 | Run autofocus once, then check its result. |
| Missing or invalid | Run autofocus to obtain a result. |

The latest light/snapshot image or completed autofocus determines the value. A newer autofocus result takes precedence over an older image. After autofocus, a usable result at or below the limit succeeds; a result above the limit, missing result, or invalid result fails the instruction. **Abort on Error** is the default. NINA's normal instruction error/retry settings remain available; Field Kit does not loop indefinitely trying to reach the limit.

**Autofocus uses the fitted HFR from its report**, not a separate measured post-focus verification exposure. Select **Star HFR** autofocus mode. The action uses NINA's configured autofocus provider, filter settings, and normal provider retry settings. A missing or incompatible historical autofocus report causes a new autofocus run.

History is session-wide, with no filter, target, binning, or age restriction. Place the action after a measurement representative of your current setup and choose a suitable limit. The action's status box shows the decision and result; search NINA's log for **AutofocusAboveHfr** for its measurements, threshold, and outcome.

See the [autofocus action guide and design](docs/autofocus-above-hfr.md) for measurement selection, failure behavior, and compatibility details.

## Status and troubleshooting

- **Setup → Monitor status** shows the combined result. The main SAFE/UNSAFE line includes source retry countdowns and attempt numbers, exhausted checks, and recovery after retry.
- **Sources** shows each draft source's polling interval and maximum safe age.
- **Live diagnostics** shows applied source state, latest result, evidence age, missed checks, and request counters. **Trace polls for 5 minutes** adds every request result and stops automatically.
- **Recent events** retains the latest 300 session events. Retries, missed checks, recovery, state changes, and lifecycle events are logged without enabling tracing.
- Search NINA's log for **FieldKitSafety**. Use **Log snapshot**, **Copy report**, or **Export diagnostics** for troubleshooting. Exports omit endpoint URLs, credentials, headers, and response bodies; source labels remain included.

## Build, test, and release

With the .NET 8 SDK (8.0.400 or later in the 8.0 series) on Windows:

```powershell
dotnet restore Nina.FieldKit.sln --locked-mode
dotnet build Nina.FieldKit.sln -c Release --no-restore
dotnet test Nina.FieldKit.sln -c Release --no-build
```

[CI](https://github.com/theatrus/nina-field-kit/actions/workflows/ci.yml) builds and tests on Windows, including fake-clock and loopback integration tests and rendered NINA-themed dialogs. Tests use simulated or loopback endpoints and never contact observatory hardware. NINA's transitive ToastNotifications and VVVV.FreeImage dependencies currently emit NU1701 compatibility warnings. Installed-host and sequence behavior should be validated with your equipment.

Release tags use four version parts, for example `v0.1.0.0`. The [signed release workflow](.github/workflows/release.yml) builds and tests the tagged source, produces code-signed DLLs, verifies their signatures, and creates the archive, SHA-256 checksum, and NINA manifest. It produces a draft GitHub release for verification. There is no unsigned release fallback.

After verifying and publishing the release, copy its manifest to `manifests/n/NINA Field Kit/3.2.0.9001/manifest.json` in [nina-plugins-registry](https://github.com/theatrus/nina-plugins-registry) and push to `main` to update the catalog. See [release notes](docs/release-notes.md).

## Fault-server testing

The development tools include a standalone Alpaca fault server for scripted errors, malformed data, latency, stalled responses, and connection failures. The integration suite runs in CI; a separate real-time suite checks production defaults and connection renewal. See [running the fault tests](docs/running-fault-tests.md) and the [.NET HTTP audit](docs/http-failure-audit.md).

## Design and license

[Autofocus action](docs/autofocus-above-hfr.md) · [Safety-monitor design](docs/alpaca-safety-monitor.md) · [Implementation notes](docs/alpaca-safety-implementation.md) · [Fault-server integration design](docs/alpaca-fault-integration-suite.md)

Copyright 2026 NINA Field Kit contributors. [Apache License, Version 2.0](LICENSE), SPDX `Apache-2.0`. Third-party dependencies retain their own licenses.
