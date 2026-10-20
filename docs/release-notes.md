Field Kit 0.1.0.14, for NINA 3.2.0.9001 or newer.

**Sky flats at captured rotations** is a new Advanced Sequencer container. Field Kit records mechanical rotations from saved light frames automatically. Put SkyFlats or other flat instructions inside the container to repeat them at each captured angle after imaging. It groups nearby angles, preserves half-turn orientations, checks reported arrival before starting children, and saves history per profile/camera/rotator across restarts. Current and previous observing nights use local noon-to-noon boundaries.

The loop uses NINA's native nested instructions, conditions, triggers and error policies. Disabled conditions and triggers remain disabled when moving to the next rotation. Equipment/safety changes, failed moves, timeouts and cancellation prevent further acquisition. See the [rotation guide](https://github.com/theatrus/nina-field-kit/blob/v0.1.0.14/docs/sky-flat-rotations.md).

This release also includes **Temperature compensation after frame**, a trigger that moves the focuser using your measured steps-per-°C slope, and **Save autofocus reference / Restore autofocus reference**, two instructions that reuse a successful startup AF position with a temperature correction on the next night. Slopes start blank. Configure these around your normal autofocus; they do not replace it. See the [temperature guide](https://github.com/theatrus/nina-field-kit/blob/v0.1.0.14/docs/temperature-compensation.md) and [reference guide](https://github.com/theatrus/nina-field-kit/blob/v0.1.0.14/docs/autofocus-reference.md).

Published DLLs are code-signed. Validation includes automated tests, rendered NINA controls, production-default safety fault acceptance, and installed NINA simulator acquisition with FITS and restart-history checks. The simulator run exercises native flat captures; it does not validate SkyFlats' exposure algorithm or physical equipment.

Update **Field Kit** through **https://nina-plugins.psf-guard.com/** and restart NINA.
