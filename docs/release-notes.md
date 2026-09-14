NINA Field Kit 0.1.0.6, for NINA 3.2.0.9001 or newer.

Adds **Autofocus Above HFR** to the Advanced Sequencer under **NINA Field Kit**. Set an absolute Maximum HFR (default 1.7): a latest image or autofocus value at or below the limit skips autofocus; a higher or unavailable value starts autofocus. The instruction fails if the new result remains above the limit or is unusable.

Autofocus comparisons use the **fitted HFR** reported by the configured provider and require Star HFR mode. The action uses normal NINA filter and autofocus retry settings, supports cancellation, saves its limit with the sequence, and reports decisions in the action status and NINA log. See the [usage guide](https://github.com/theatrus/nina-field-kit#autofocus-above-a-fixed-hfr-limit) and [detailed action guide](https://github.com/theatrus/nina-field-kit/blob/v0.1.0.6/docs/autofocus-above-hfr.md).

Validation includes 217 automated tests and the real-time default safety-policy acceptance check. Hardware and installed-NINA visual acceptance remain separate operator checks.

Published DLLs are code-signed. The archive includes the DLLs, README, and Apache-2.0 license; the manifest and SHA256SUMS.txt identify the archive. Alpaca requests now identify this release as `NINA-Field-Kit/0.1.0.6`.

Update **NINA Field Kit** through **https://nina-plugins.psf-guard.com/** and restart NINA.
