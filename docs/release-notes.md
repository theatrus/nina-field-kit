Field Kit 0.1.0.18, for NINA 3.2.0.9001 or newer.

**Twilight flats by filter and rotation** runs NINA's built-in **Twilight Sky Flats** at every mechanical rotation recorded from the night's saved light frames, completing all rotations on one filter before selecting the next. Choose your light-to-dark filter order explicitly; the helper does not reverse it automatically.

Add **NINA Field Kit → Twilight flats by filter and rotation**, choose **Setup…** to enable and order filters, and place **Flat Device → Twilight Sky Flats** in its normal Instructions section. Set the native instruction's amount, exposure range, mean target, gain and binning as usual. The helper supplies the filter for each pass. Do not nest the separate captured-rotation helper inside it.

The compact sequence view shows the chosen filter order, observing night and execution status. The NINA-themed setup dialog keeps readable filter names beside native ON/OFF switches, with ordering arrows, rotation preview, and expandable movement settings. **Apply** validates and saves your draft to the sequence; **Cancel** discards it. Rotation-history errors appear in the preview without closing NINA.

The filter/angle plan and movement settings are frozen for each run. Failed moves, cancellation, stale filter definitions and equipment/profile changes stop before the next flat. Original native instruction filter settings are restored afterward without additional hardware movement.

Validation includes automated fault and sequencing tests, three-theme WPF rendering and installed-NINA simulator runs with the real Twilight Sky Flats action across a restart. Simulator brightness is fixed; changing real twilight conditions still require observing validation. See the [setup guide](https://github.com/theatrus/nina-field-kit/blob/v0.1.0.18/docs/twilight-filter-rotations.md) and [simulator acceptance guide](https://github.com/theatrus/nina-field-kit/blob/v0.1.0.18/docs/nina-simulator-acceptance.md).

Published DLLs are code-signed. Update **Field Kit** through **https://nina-plugins.psf-guard.com/** and restart NINA.
