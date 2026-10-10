# Temperature compensation after frame

Status: implemented for the next release; not in 0.1.0.13.

This Advanced Sequencer trigger adjusts the focuser using its reported temperature and a measured steps-per-°C slope. It runs after completed light exposures, including the last light frame in a container. It does not run full autofocus or react to calibration frames, failed exposures, or skipped exposures.

## Setup

1. Connect a focuser that reports temperature to NINA.
2. Measure the signed slope for your optical setup. Add **NINA Field Kit → Temperature compensation after frame** to your imaging container's **Triggers** and enter **Steps / °C**. New triggers start blank; a finite, non-zero slope is required.
3. Disable hardware temperature compensation and any other instructions or triggers that move focus for temperature. Regular full-autofocus triggers can still run when needed.
4. Run autofocus before the first imaging exposure, then take light frames normally.

For example, if measured best focus is 10,000 steps at 10 °C and 9,900 steps at 9 °C, the slope is `(9900 - 10000) / (9 - 10) = +100 steps/°C`. A 0.25 °C fall then requests a 25-step decrease from the established focus. This is an example, not a default or calibration for your equipment.

The temperature is the **focuser's** reading, not camera cooling temperature or a weather-station measurement. The slope should describe your complete optical setup and the focuser's position convention. One trigger uses the same slope for all frames in its scope.

## Relative compensation and autofocus

Field Kit delegates to `IFocuserMediator.MoveFocuserByTemperatureRelative`, the same method used by NINA's built-in relative temperature instruction. NINA owns the shared baseline, accumulated fractional steps, travel-limit handling, movement cancellation, and configured settling time. The sequence waits for this method before continuing.

NINA resets the baseline after successful autofocus and user focusing. Field Kit observes those notifications so a previous identical temperature cannot suppress a correction against the new baseline. Without a prior baseline, NINA's first relative call initializes it with zero temperature correction; this does not establish good focus, so run autofocus first. Starting or cloning the trigger does not replace NINA's shared baseline.

The trigger checks temperature after each completed light frame. After a successful call, an identical temperature skips another move. Fractional corrections are retained by NINA when a changed temperature produces less than a whole step. There is no separate temperature threshold or guessed initial slope.

## Target Scheduler

For Field Kit's trigger, add it to the **Triggers** section of the **Sequential Instruction Set containing Target Scheduler Container**. NINA forwards after-instruction trigger checks through the parent containers, so the trigger sees completed light exposures inside the scheduler. It does not depend on target coordinates. Keep your normal startup autofocus and other desired autofocus triggers.

You can use NINA's existing instruction today instead:

1. Expand **Target Scheduler Container → Custom Event Containers → After Each Exposure**.
2. Drag **Focuser → Move Focuser by Temp.** into that area.
3. Select **Relative** mode and enter your measured slope.
4. Run autofocus before imaging and disable other temperature compensation.

Do not enable both the Field Kit trigger and that instruction. Target Scheduler runs its After Each Exposure instructions after the image completes the processing pipeline. Its Exposure Templates describe acquisition settings; the compensation belongs in the sequencer, rather than an Exposure Template.

References: [Target Scheduler custom event instructions](https://tcpalmer.github.io/nina-scheduler/sequencer/container.html#custom-event-instructions), [sequence item and trigger placement](https://tcpalmer.github.io/nina-scheduler/sequencer/notes.html), [NINA temperature instruction source](https://github.com/isbeorn/nina/blob/release/3.2.x/NINA.Sequencer/SequenceItem/Focuser/MoveFocuserByTemperature.cs).

## Status and failures

The compact native NINA trigger row retains its title, drag handle, menu, and enable/disable controls. It shows the configured slope and last temperature/actual step change. Execution start, temperature, slope, initial and final position, cancellation, and errors are logged under **TemperatureCompensationTrigger** in the NINA log.

A connected unsafe safety monitor or a focuser that is moving or settling defers compensation until a later light frame. An unconnected safety monitor does not block it. Missing/non-finite temperature, a disconnected focuser, an invalid slope, or enabled hardware temperature compensation prevents a move and appears in validation/status. Execution rechecks equipment state before issuing the movement.

Movement exceptions and a negative failure return propagate through NINA's trigger failure handling. Cancellation is passed to the native method. Failed or cancelled moves are not recorded as successfully applied temperatures. A reconnect, autofocus, manual focus, or slope edit clears the trigger's duplicate-temperature suppression.

The tests use strict mediator mocks and the real NINA sequential execution strategy to verify that compensation happens after a completed frame and finishes before the next instruction. No test connects to observatory hardware. Validate the measured slope and settling behavior with your focuser before relying on compensation for a session.
