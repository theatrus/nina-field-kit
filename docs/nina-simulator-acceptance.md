# Installed NINA simulator acceptance

This test runs the installed NINA application, rather than replacing equipment mediators in a unit test. It uses NINA's simulator camera and the ASCOM rotator simulator. It creates a new profile with all other equipment unselected, launches NINA's Advanced Sequencer through its supported command-line options, and checks actual saved FITS files and NINA diagnostics.

## Run

Install NINA **3.2.0.9001** or newer, ASCOM Platform and its rotator simulator. Field Kit must already be installed in the usual `Nina.FieldKit` plugin directory. Close NINA before starting.

```powershell
dotnet restore Nina.FieldKit.sln --locked-mode
dotnet build Nina.FieldKit.sln -c Release --no-restore
./tools/Run-NinaRotationAcceptance.ps1
```

Use `-NinaPath` for another NINA executable location, or `-OutputDirectory` for a fresh evidence directory. Each NINA process has a ten-minute deadline, configurable with `-TimeoutSeconds`. The test temporarily installs the Release build and restores the previous plugin DLLs on completion or failure. It archives the generated profile and removes that test profile from NINA; existing equipment profiles are not edited. Only run one acceptance test at a time.

## Assertions

| Stage | Expected result |
| --- | --- |
| First host run | Connect simulator camera and rotator; save LIGHT frames at 12°, 12.04°, 192° and 45°. |
| Rotation plan | Three visits: 12°, 192°, 45°. The nearby 12.04° light groups with 12°; the half-turn stays separate. |
| Flat acquisition | A native nested instruction set with a two-iteration loop saves two FLAT frames at each rotation. |
| Application restart | Start NINA again with the same generated profile and a flats-only sequence. No new lights are taken. |
| Persistent history | The second host run uses the same three angles and saves another six FLAT frames. |
| Final evidence | Four LIGHT files, twelve FLAT files, simulator camera identity in FITS, and four FLAT headers at each planned mechanical angle across both runs. Both loops log successful completion without Field Kit cancellation/failure. |

The evidence directory contains `result.json`, both input sequences, both NINA logs, the final profile, plugin backups and saved FITS frames. The generated profile's options contain the persisted rotation history. A failed assertion returns an error and retains diagnostic evidence. Normal NINA plugin discovery can log unrelated network/catalog errors; acceptance checks the rotation loop's own diagnostics and saved images.

Disabled instructions/conditions/triggers, ancestor conditions, equipment changes, unsafe states, failed/cancelled moves and timeouts are covered by the automated regression suite. NINA does not serialize a condition's disabled status, so that runtime selection is exercised in unit tests instead of attempting to seed it through sequence JSON.

The installed-host test uses native **Take Exposure** FLAT instructions. It validates the container and nested-loop integration without requiring the separate SkyFlats plugin, sky brightness, a real mount or physical rotator. SkyFlats' exposure algorithm and actual equipment still require their own observing validation. CI builds the acceptance generator and runs the unit/loopback suite; the full installed-host run requires a local NINA/ASCOM installation.

This acquisition test loads a prepared sequence file; it does not exercise mouse editing. UI regression coverage separately hosts NINA's actual `SequenceView` and an embedded `ItemsControl`. It checks visible children, a single native instruction drop target, sidebar cloning, reparenting, nested sequential/parallel sets, reordering, removal, expansion in both directions and save/reload. It also checks rejection of conditions/triggers in the instruction area and disabled-control behavior. This is control-level testing, not an automated physical mouse drag in the installed application.
