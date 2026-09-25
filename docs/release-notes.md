Field Kit 0.1.0.11, for NINA 3.2.0.9001 or newer.

Adds **NINA Field Kit → Slew to sky-flat point** to the Advanced Sequencer. Based on SkyFlats' null-point instruction, it targets **75° altitude opposite the Sun**, calculated at execution using your NINA profile location, then enables tracking after a successful slew. The mount must already be connected and unparked.

Includes the mount-compatibility fix from SkyFlats PR #4: mounts without native Alt/Az slewing use NINA's RA/Dec conversion. Failed slews and failures to enable tracking fail the instruction. Cancellation is passed through to NINA. The instruction uses the standard draggable layout and reports progress in the UI and NINA log.

This instruction positions the mount only; it does not acquire flats or adjust exposures. SkyFlats does not need to be installed. See the [usage guide](https://github.com/theatrus/nina-field-kit/blob/v0.1.0.11/README.md#slew-to-sky-flat-point). Imported SkyFlats files retain MPL-2.0 licensing; attribution and license text are included in the package.

Published DLLs are code-signed. Release validation runs the automated tests and real-time default safety-policy acceptance check. Hardware slewing has not been exercised by these tests.

Update **Field Kit** through **https://nina-plugins.psf-guard.com/** and restart NINA.
