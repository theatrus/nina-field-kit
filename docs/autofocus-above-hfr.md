# Autofocus Above HFR

Status: implemented on main; not included in release 0.1.0.5.

## Purpose and sequence placement

Add **NINA Field Kit → Autofocus Above HFR** to the Advanced Sequencer after an image or an autofocus instruction. Set **Maximum HFR** to the absolute limit you accept (default 1.7). Place the action inside the imaging loop to evaluate each iteration. This is an instruction evaluated at its position in the sequence, not a continuously running trigger.

With a limit of 1.7, a latest HFR of 1.6 or 1.7 skips autofocus. A value of 3 starts autofocus. If the returned autofocus fit HFR is 1.7 or lower, the instruction succeeds. A result of 3 fails the instruction. The default instruction error behavior is Abort on Error; users can change NINA's normal instruction retry/error policy.

## Measurement selection

Use the newest light/snapshot image or completed autofocus marker in the current NINA history, ordered by timestamp. Autofocus wins a timestamp tie. Calibration frames are excluded. A missing, zero, negative, NaN, or infinite value causes autofocus; do not search backward for an older acceptable value. History is session-wide and is not filtered by target, filter, binning, or age. Place the action after a measurement representative of the current imaging setup, and choose a limit appropriate to that setup.

NINA 3.2's autofocus history marker reuses the previous image object. Its image HFR is not the autofocus result. For an existing autofocus marker, read its matching report from NINA's AutoFocus report directory, restricted to the active profile, date, exact report timestamp, and filter. Search at most 256 matching filenames and reject report files larger than 1 MiB. Missing or unreadable reports mean unknown HFR and cause a new autofocus run. No report from a different profile or unmatched timestamp is accepted.

The autofocus metric is the report's **CalculatedFocusPoint.Value**, labeled **Autofocus fit**. This is the fitted HFR, not a separate measured post-focus verification exposure. NINA 3.2's report API does not expose that final verification measurement. Contrast autofocus reports cannot be compared to an HFR limit. The action requires Star HFR autofocus mode. Other autofocus providers work when they return a compatible Star HFR report; existing history reports without the standard naming/schema are treated as unavailable.

## Execution and failure handling

Freeze the threshold when execution starts. For a value above the limit or unknown, validate the camera, focuser, and Star HFR configuration, then create the configured NINA autofocus provider through IAutoFocusVMFactory. Use the selected filter's profile settings and NINA's normal autofocus window, cancellation token, progress reporting, and provider retry settings.

Call the autofocus provider once per instruction execution. The provider may make its own configured attempts. Record a completed report in NINA history, then enforce the absolute limit. A null, invalid, non-HFR, or above-limit result fails. Do not create an internal retry loop. NINA's instruction-level retry policy can retry the action if explicitly configured. Cancellation propagates, rejects late results, and closes the autofocus window. Failure does not attempt to restore the previous focus position; the autofocus provider owns its equipment operations.

The action shows a separate status box below muted help text. NINA logs contain a correlation ID, threshold, measurement source/value, skipped/started/accepted/failure/cancellation decision. Settings are persisted with the sequence; transient status is not.

## Validation

Automated tests cover exact-threshold acceptance, high and missing HFR, bad and null AF results, invalid thresholds, cancellation, settings frozen during execution, disconnected equipment, sequence serialization/cloning, MEF discovery, WPF template loading, real report selection, newer image precedence, and the native provider adapter. Hardware and installed-NINA visual acceptance remain manual; tests do not command real equipment.

## NINA API references

The adapter targets the pinned NINA 3.2.0.9001 packages. Relevant upstream 3.2 sources: [RunAutofocus](https://github.com/isbeorn/nina/blob/release/3.2.x/NINA.Sequencer/SequenceItem/Autofocus/RunAutofocus.cs), [image history](https://github.com/isbeorn/nina/blob/release/3.2.x/NINA/ViewModel/ImageHistory/ImageHistoryVM.cs), [history point](https://github.com/isbeorn/nina/blob/release/3.2.x/NINA.WPF.Base/Model/ImageHistoryPoint.cs), and [autofocus report](https://github.com/isbeorn/nina/blob/release/3.2.x/NINA.WPF.Base/Utility/AutoFocus/AutoFocusReport.cs).
