# Twilight flats by filter and rotation

Status: implemented on the companion feature branch, not included in the 0.1.0.17 Cloud guard release. Requires NINA 3.2.0.9001 or newer.

This container runs NINA's built-in [Twilight Sky Flats](https://github.com/isbeorn/nina/blob/release/3.2.x/NINA.Sequencer/SequenceItem/FlatDevice/SkyFlat.cs) at every recorded mechanical rotation for one filter before selecting the next filter. The list runs top to bottom, so your light-to-dark order is preserved across the whole flats session. It reuses Field Kit's existing rotation history and movement checks; no SkyFlats plugin is required.

## Setup

1. Connect the camera, filter wheel and rotator. Configure filter names and positions in the active NINA profile.
2. After imaging, wait for your flat conditions and position the mount, for example with **Slew to sky-flat point**.
3. Add **NINA Field Kit → Twilight flats by filter and rotation**. This container already includes the rotation loop.
4. Check the filters to include. New instances start with all known profile filters enabled. Use each row's **↑ / ↓** buttons to set the exact order you want. This never changes physical slot order or automatically reverses the list.
5. Inside its normal **Instructions** section, add **Flat Device → Twilight Sky Flats**. Set its amount, exposure range, target mean, tolerance, gain, offset, binning and optional dither as usual.
6. Choose **Current night** or **Previous night**, review tolerance/move timeout, and use **Show rotations** to preview the angles.

The container supplies the selected filter to every enabled native Twilight Sky Flats instruction while running, overriding that instruction's own **Filter** selector. Its original filter setting is restored afterward. Other twilight settings remain owned by the native instruction. You can put native twilight instructions in a nested sequential set and include positioning instructions you want repeated at each angle.

Do not add a separate **Sky flats at captured rotations** inside this container; that would repeat the rotation loop twice. A separate **Switch Filter** instruction also conflicts with this container's filter selection. Validation reports either configuration.

```text
Target Scheduler / normal imaging
Wait for flat conditions
Slew to sky-flat point
Twilight flats by filter and rotation
  Filter order: R, L             (example user-selected order)
  Instructions:
    Twilight Sky Flats          (native exposure settings, amount per angle)
Normal shutdown
```

For recorded angles 12°, 192° and 45°, that example runs:

```text
R: 12° → 192° → 45°
L: 12° → 192° → 45°
```

## Saved settings and updates

Filter selection and order are saved in the sequence and survive copying or NINA save/reload. **Refresh profile filters** retains matching filters in your saved order, removes definitions no longer present, and adds new filters unchecked when refreshing an existing list. Review the order after refreshing. Without a refresh, a renamed or moved enabled filter fails validation rather than being silently mapped to another slot.

The enabled filter order, observing-night angle plan, tolerance and timeout are copied when the run starts. Later edits or late light-frame saves affect the next run. Filter-order controls are disabled while the loop executes.

## Execution and limits

The existing [rotation guide](sky-flat-rotations.md) describes recording, noon-to-noon observing nights, angle grouping, scope isolation, conditions and native instruction behavior. Every selected filter visits the same full angle list; this does not infer which filters were used at which angles or mark Target Scheduler flats complete.

Before switching a filter or moving the rotator, Field Kit requires stationary connected equipment, no active camera exposure and safety permission. It checks the selected wheel/profile, filter identity and reported filter arrival. A failed, timed-out or cancelled move cannot start the next flat. Changing profiles stops the run. The move timeout applies separately to each filter change and each rotator move.

An empty rotation history skips without moving the filter wheel or rotator. A stopping ancestor condition or skipped child prevents subsequent filters. Conditions on this container have the existing per-rotation semantics; put a condition that should end the entire flats session on its enclosing container. Native instruction retries and error policies remain in effect.

The native Twilight Sky Flats instruction owns exposure selection, twilight adaptation, image acceptance, progress and optional dithering. The helper does not add a second brightness algorithm or guarantee that twilight conditions will permit every requested flat. Re-running repeats the whole selected plan. It leaves the wheel and rotator at their last positions; cleanup restores instruction settings without extra hardware movement.

The UI shows the active filter and rotation status. NINA logs under **TwilightFlats** record the frozen plan, filter moves, per-filter completion, cancellation and failure; **SkyFlatRotations** records angle movement and child execution.

Tests cover filter-first execution through the native sequential strategy, progress reset, ordering/enabling, stale definitions, move failures/timeouts, cancellation, profile/equipment changes, conditions, disabled children, fixed plans, native serialization and MEF discovery. WPF checks exercise the actual container and native twilight views in tree and embedded hosts, ordering/checkbox bindings, expansion and instruction drops in three NINA themes. The [installed-host test](nina-simulator-acceptance.md) supports `-TwilightFilters` to run the real native action with simulator camera, filter wheel and rotator, checking FITS filter/angle order across a restart. Physical mouse dragging and changing real twilight brightness require separate observing validation.
