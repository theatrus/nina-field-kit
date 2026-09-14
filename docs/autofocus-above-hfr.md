# Autofocus Above HFR trigger

Implemented on main. The published 0.1.0.8 release has the previous sequence-item implementation.

## Use

Add **NINA Field Kit → Autofocus Above HFR** to an imaging container's **Triggers**, alongside normal NINA triggers such as AF after HFR. Enter **Maximum HFR**; there is no default. A new or cleared trigger requires a positive limit before it can run. Saved explicit limits are preserved. It uses an absolute limit rather than a percentage increase.

Before the next LIGHT exposure, compare the latest light/snapshot image or completed autofocus HFR. At or below the limit: do nothing. Above the limit: run autofocus once and check the returned result. Missing or invalid HFR: wait for a valid measurement. Do not trigger before non-exposure instructions, calibration frames, or the end of the sequence. Defer if a connected safety monitor reports unsafe or the estimated autofocus plus next exposure is too close to a meridian flip, using NINA's native scheduling check.

## Autofocus and failure

The configured NINA autofocus provider owns filter changes, equipment operations, and its normal retries. The trigger passes cancellation and progress through. A result still above the limit, invalid/non-HFR result, or null report fails through NINA's trigger failure handling. There is no internal repeat-until-good loop. A later acceptable result suppresses the next trigger check. Safety is rechecked immediately before starting autofocus.

The compact template uses NINA's SequenceBlockView so the trigger retains its title, drag behavior, and menu. It matches the built-in AF-after-HFR layout: a single inline limit field and right-aligned status. Help is in tooltips. Threshold settings survive sequence serialization and cloning; transient results are not saved.

## Measurement details

NINA 3.2's autofocus marker reuses the previous image object. That object's HFR is not the autofocus result. The reader selects by timestamp, with autofocus winning ties, and matches a report by active profile, date, exact timestamp, and filter. It reads at most 256 candidate filenames and rejects reports larger than 1 MiB. Unavailable or invalid matching reports do not trigger autofocus until a usable measurement becomes available. It does not search backward for an older good value.

Autofocus comparisons use **CalculatedFocusPoint.Value**, the fitted HFR, not a separate post-focus verification exposure. Select Star HFR mode. Contrast values cannot be compared to this limit. Alternative autofocus providers need compatible reports. Image history is session-wide and not restricted by target, filter, binning, or age. Use a representative measurement and limit for your setup.

## Migration and validation

Replace the old **Autofocus Above HFR** instruction in the sequence body with this trigger in the container's Triggers. The old CLR instruction type remains available for compatibility, but is no longer exported in the instruction picker. Existing sequence instructions are not automatically relocated or converted.

Tests cover trigger-only MEF discovery, before-LIGHT gating, equality and invalid readings, no-history behavior, unsafe deferral, result rejection, cancellation, serialization/cloning, suppression after successful autofocus, and native WPF controls. The existing autofocus adapter tests cover report selection and provider execution. Rendering tests use mock profiles and do not command hardware. Installed-host drag/drop and hardware execution still require operator verification.

API references: [NINA autofocus trigger](https://github.com/isbeorn/nina/blob/release/3.2.x/NINA.Sequencer/Trigger/Autofocus/AutofocusAfterExposures.cs), [trigger lifecycle](https://github.com/isbeorn/nina/blob/release/3.2.x/NINA.Sequencer/Trigger/SequenceTrigger.cs), [image history](https://github.com/isbeorn/nina/blob/release/3.2.x/NINA/ViewModel/ImageHistory/ImageHistoryVM.cs), and [autofocus report](https://github.com/isbeorn/nina/blob/release/3.2.x/NINA.WPF.Base/Utility/AutoFocus/AutoFocusReport.cs).
