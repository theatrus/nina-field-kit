Field Kit 0.1.0.16, for NINA 3.2.0.9001 or newer.

**Sky flats at captured rotations** now uses NINA's self-contained container view. Instructions appear inside its normal **Instructions** section, the header expands and collapses the contents, and nested sequential and parallel sets display their children. Add instructions with the native **+** control or drag them into the empty section; use the normal insertion positions when it already contains instructions. Edit the sequence while it is stopped.

The previous layout relied on the surrounding tree to render children and added a second drop area. That left embedded editors without a visible child list and made drop targets overlap. The extra drop box has been removed. Rotation recording and execution are unchanged. See the [rotation guide](https://github.com/theatrus/nina-field-kit/blob/v0.1.0.16/docs/sky-flat-rotations.md).

Published DLLs are code-signed. Regression checks cover visible children in NINA's tree and embedded editors, expansion, native drops, nested sets, reordering, removal and save/reload. The automated suite and production-default safety acceptance run in CI.

Update **Field Kit** through **https://nina-plugins.psf-guard.com/** and restart NINA.
