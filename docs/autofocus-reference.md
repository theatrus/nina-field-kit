# Nightly autofocus references

Status: implemented for the next release; not in 0.1.0.13.

An OTA can finish a cool night far from the focus position needed at the next warm startup. These two sequence instructions preserve a successful startup autofocus reference and use temperature to predict the next night's starting position. The normal autofocus still runs to find precise focus.

## Nightly sequence

After equipment is connected, wrap your existing startup autofocus with these instructions:

1. **Switch Filter** to your autofocus filter, if you have a filter wheel.
2. **NINA Field Kit → Restore autofocus reference**; enter your measured signed **Steps / °C**.
3. **Focuser → Run Autofocus**, using your normal autofocus provider and settings.
4. **NINA Field Kit → Save autofocus reference**.
5. Continue imaging normally.

Run this block once in your nightly startup. Do not put Save in the exposure loop, in an after-frame trigger, or after every autofocus throughout the night: that would replace the warm startup reference with later cool-night results. Target Scheduler's **Before New Target** event runs for each new target, so an unguarded save there also updates the reference repeatedly. Place these actions wherever your once-per-night startup AF currently runs.

On the first night, Restore has no reference. It reports **No saved reference — run autofocus**, issues no movement, and lets the next instruction run. Successful autofocus followed by Save creates the initial reference. Keep normal autofocus failure handling enabled so a failed startup AF does not continue into Save.

## Temperature adjustment

Restore uses:

```text
target = saved position + steps per °C × (current temperature − saved AF temperature)
```

For example, a saved reference of **10,000 steps at 10 °C**, a current temperature of **20 °C**, and a measured slope of **−100 steps/°C** predicts **9,000 steps**. The current position left after the previous night's cooling does not affect the prediction. If temperature falls instead, that negative slope increases position.

The slope starts blank and must be a finite, non-zero value. Use your measured optical-system slope; these example values are not defaults. If using Field Kit's after-frame temperature trigger as well, use the same calibrated slope where appropriate. The reference stores the successful AF's temperature; restore reads the focuser's current temperature, not camera cooling temperature or a weather sensor. Predictions round to the nearest whole step, with halves away from zero.

## What is saved

Save reads the latest successful AF from NINA's AF history. It records the actual settled focuser position, AF temperature and timestamp, profile ID, focuser device ID, filter name, and filter slot. The current position must still agree with the AF fit within one step; this allows rounding of the fitted position while rejecting an unrelated later focuser move.

The AF must be no more than 30 minutes old, and when the sequence is initialized it must have completed after that initialization. Save does not search backward for an older usable result when the latest result is invalid or for another filter. Place it immediately after the successful startup AF.

The AF filter must remain selected. If your provider switches to a dedicated AF filter and restores the original imaging filter afterward, select that dedicated AF filter before the whole startup block. Save will reject a result for a different selected filter rather than guessing a filter offset.

References persist in NINA's profile plugin settings, surviving application restarts. Each profile/focuser/filter combination has a separate reference. No filter wheel is supported when none is configured. A configured wheel must be connected; disconnecting it does not fall back to a no-filter reference. Sequences save only the Restore slope and normal instruction settings; they do not embed equipment-specific reference positions.

Restore never overwrites the reference. A later successful execution of Save replaces that combination's reference. Changing the focuser's zero point, optical train, or calibration can invalidate a reference; run a fresh AF and Save for the changed setup.

## Movement and failures

Restore calls NINA's absolute focuser movement method and waits for its normal backlash compensation and settling. It checks that the prediction fits both the supported integer position range and the focuser's reported travel limits before issuing movement. A negative failure return or a returned position different from the requested position fails the action.

A disconnected/busy focuser, enabled hardware temperature compensation, moving/disconnected configured filter wheel, unavailable temperature, active camera exposure, or connected unsafe safety monitor prevents restoration. Missing references skip normally; corrupt, incomplete, or mismatched stored data raises an error. References are not silently treated as position zero or temperature zero.

Both actions default to NINA's **Abort on error** policy and support the normal instruction attempts/error controls. They forward cancellation and preserve the stored reference when restoration fails or is cancelled. Changing profiles during a restore cancels the native move; equipment/profile/filter identity is checked again before success is reported.

Restore is a prediction, not a successful autofocus result. It does not publish a fake AF or manual-focus notification. The real AF that follows resets NINA's temperature-compensation baseline as usual.

## Diagnostics and tests

The native NINA sequence rows keep their title, drag handle, menu, and normal controls. Restore shows the slope field, initially blank. Both actions show their outcome and saved/restored position and temperature; full error text is available in the status tooltip.

Search the NINA log for **SaveAutofocusReference** or **RestoreAutofocusReference**. Logs include the saved reference, planned target, current temperature, slope, movement outcome, cancellation, and errors.

Automated tests cover signed temperature adjustment, persistence across service recreation, profile/focuser/filter isolation, first-night skip, stale/invalid AF rejection, missing/corrupt stored fields, travel limits, unsafe/busy equipment, cancellation, profile changes, movement failures, serialization, MEF discovery, and rendered native sequence controls. Tests use simulated state and never contact observatory hardware.
