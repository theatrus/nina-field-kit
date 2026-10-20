# Sky flats at captured rotations

Status: available in Field Kit 0.1.0.14. Built against NINA 3.2.0.9001.

This container visits the night's captured mechanical rotator positions and runs the instructions inside at each position. It lets an existing SkyFlats instruction handle sky-flat acquisition after Target Scheduler imaging, and also accepts other instructions or nested instruction sets. It does not require a Target Scheduler installation or access its database.

## Example sequence

```text
Imaging instruction set
  Target Scheduler Container

Wait for suitable morning flat conditions
Slew to sky-flat point                  (or another positioning instruction)
Sky flats at captured rotations         (Field Kit container)
  SkyFlats                              (configure your filters and flat settings)
  Any other per-rotation instructions

Park / normal shutdown instructions
```

Add **NINA Field Kit → Sky flats at captured rotations** as a container, then drag instructions onto **Drop instructions here**. This explicit drop area is available in **0.1.0.15** and stays available after the first instruction is added. It accepts sidebar instructions, existing instructions and nested instruction sets, appending them to the loop. Add conditions and triggers in their own native sections. Use the normal NINA header to rename, drag, duplicate or remove the container. The example's SkyFlats instruction comes from that separate plugin. Field Kit does not install it or choose exposure settings. A positioning instruction can also be placed inside the container if you want it repeated/recalculated for every angle.

Field Kit must be loaded while the lights are acquired. It automatically observes NINA image saves for the active profile; no extra trigger or recording instruction is required. Both ordinary Advanced Sequencer and Target Scheduler light frames use this path.

## Controls

| Control | Default | Behavior |
| --- | --- | --- |
| Night | Current night | Computer-local noon to the following noon. At 06:00, this means the night that began yesterday. |
| Previous night | Optional | The noon-to-noon period immediately before Current night; useful after noon. |
| Tolerance ° | 0.1° | Group nearby positions and verify that the rotator actually reaches the requested angle within this tolerance. Range 0.01–5°. |
| Move timeout s | 180 s | Cancellation deadline passed to NINA's native mechanical move. Range 5–1800 s. |
| Show rotations | Button | Display the observing date and the currently recorded distinct angles without commanding equipment. |

For example, lights at mechanical positions 12.00°, 12.04°, 192.00° and 45.00° produce three visits with the default tolerance: 12°, 192° and 45°. Positions around 359.98°/0.02° group across the wrap point; a half turn remains a separate flat orientation. Grouping keeps the first recorded representative, rather than rounding every requested angle to a grid.

The container takes a fixed copy of the selected plan and motion settings when it starts. Allow pending light-frame saves to complete first. New save events or edits during the loop do not add angles to that run. Each iteration resets the children and executes them through NINA's native sequential strategy, including nested instruction sets, block lifecycle events, triggers and error policies. Disabled instructions, conditions and triggers remain disabled between rotation visits. Conditions on this container run per rotation, as they do for an instruction set; put limits that should stop the whole flats session on its enclosing container. An ancestor condition that ends the session prevents the next move. A skipped child stops further rotation visits.

An empty night, or a body with no enabled instructions, skips without moving. The container leaves the rotator at the last visited position and performs no extra restoration movement during cleanup. Re-running the container repeats the selected angles; it does not track how many flats were completed.

## Motion and safety

Before moving, Field Kit requires the recorded profile/camera/rotator scope, a connected stationary rotator, no active camera exposure, and a safe connected safety monitor if one is selected. A configured but disconnected safety monitor blocks movement. Changing profiles cancels and fails the loop. Changing the camera or rotator selection stops subsequent movement.

Moves use `IRotatorMediator.MoveMechanical`, rather than a plate-solved sky position or position angle. NINA may substitute an angle to satisfy rotator limits; Field Kit checks that substitution first and refuses a different mechanical orientation. It checks NINA's reported mechanical position again after moving. Flat instructions do not start when the move is cancelled, reports an invalid result, or finishes outside tolerance. This verifies reported driver state; it is not an independent encoder measurement. The native mechanical-move implementation handles cancellation/Halt, subject to driver cooperation. A hung driver cannot be repaired by this container.

The loop does not slew or park the mount, assess sky brightness, choose filters, calculate flat exposures, or change the rotator's configured limits. Put those operations in your sequence or flat instruction. It checks safety around each move; keep normal NINA unsafe handling configured to interrupt child exposures when necessary. Child instructions should leave rotation under this container's control.

## Recording and limits

Saved **LIGHT** frames contribute the image metadata's `Rotator.MechanicalPosition`. Synced rotator `Position` and target sky position angle are not substituted. Flats, darks, missing/invalid mechanical positions and delayed saves from a previous profile are ignored. Valid 0° is retained. Images whose exposures began before recording started or before the active profile was selected are not imported.

History is stored in NINA's profile plugin settings, keyed by profile, camera ID and rotator ID, and survives NINA restarts. Samples within 0.01° share an entry. Old samples are pruned when recording new rotations, with a seven-day history window and a 4096-sample bound. The container's tolerance groups the retained samples for a particular run. The local observing date is based on each exposure's start time, so frames saved after midnight or noon still belong to the correct observing night. Keep the computer's time zone consistent during an observing session.

This version records images observed while Field Kit is loaded. It does not import pre-existing FITS/XISF files or Target Scheduler history, predict the mechanical positions of upcoming targets, select only accepted images, deduplicate filter/exposure-specific flat sets, or mark Target Scheduler flats complete. All recorded light rotations for the selected equipment/night are visited; configure the child flat instruction to acquire the filter/camera combinations you need at each one. If equipment is physically reconfigured without changing profile or driver IDs, older recorded orientations may no longer describe the optical train.

The status line and **Show rotations** provide the date/angle list and active iteration. NINA log entries under **SkyFlatRotations** include `RotationRecorded`, `Plan`, `MoveStarted`, `InstructionsStarted`, `Completed`, cancellation and failure. Corrupt history and recording failures in the current session block that night's plan rather than treating an incomplete list as complete; recording failures do not throw into NINA's image-save pipeline.

Tests exercise native save events, history persistence and equipment isolation, noon/DST boundaries, angle wrap and half-turn separation, repeated/nested children, disabled items/conditions/triggers, ancestor conditions, profile changes, equipment/safety gates, unsuccessful/cancelled moves, fixed plans, NINA serialization and MEF discovery. CI also renders the real NINA container and child instructions. The [installed-host simulator acceptance](nina-simulator-acceptance.md) launches NINA twice and verifies saved LIGHT/FLAT FITS angles and persistent history. Physical equipment and SkyFlats' own acquisition algorithm require separate validation.
