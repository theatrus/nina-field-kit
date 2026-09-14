Field Kit 0.1.0.9, for NINA 3.2.0.9001 or newer.

**AF above HFR** is now an Advanced Sequencer trigger. Before the next light exposure, it runs autofocus when the latest image or completed autofocus HFR exceeds your chosen absolute limit. It defers when the connected safety monitor reports unsafe or NINA's meridian-flip scheduling check requires it. An autofocus result that remains above the limit fails through NINA's trigger failure handling.

The compact layout follows NINA's built-in HFR trigger, with a draggable title, inline Maximum HFR field, and status on the right. New triggers start blank and require an explicit positive limit; clearing the field disables the trigger. Saved explicit limits are preserved.

Replace the old **Autofocus Above HFR** sequence instruction with **NINA Field Kit → AF above HFR** in your imaging container's **Triggers**. Existing instructions are not automatically converted. See the [usage guide](https://github.com/theatrus/nina-field-kit/blob/v0.1.0.9/docs/autofocus-above-hfr.md) for measurement details and setup.

Published DLLs are code-signed. Release validation runs the automated test suite and the real-time default safety-policy acceptance check.

Update **Field Kit** through **https://nina-plugins.psf-guard.com/** and restart NINA.
