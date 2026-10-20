Field Kit 0.1.0.15, for NINA 3.2.0.9001 or newer.

**Sky flats at captured rotations** now has a visible **Drop instructions here** box. Drag SkyFlats, another instruction or a nested instruction set onto that box to append it to the rotation loop. It accepts both new sidebar instructions and existing instructions, and stays available after the first child is added. Edit the sequence while it is stopped.

The custom settings area previously offered no clear instruction drop target. The new area uses NINA's native drag-over and drop handling, preserves normal instruction cloning/reparenting, and keeps conditions and triggers in their own sections. Rotation recording and execution are unchanged. See the [rotation guide](https://github.com/theatrus/nina-field-kit/blob/v0.1.0.15/docs/sky-flat-rotations.md).

Published DLLs are code-signed. Regression coverage now hosts the actual NINA sequencer control, hit-tests the new drop area, and executes native drops for sidebar instructions, existing instructions and nested sets. It also checks that incompatible conditions/triggers and disabled controls reject drops. The automated suite and production-default safety acceptance run in CI.

Update **Field Kit** through **https://nina-plugins.psf-guard.com/** and restart NINA.
