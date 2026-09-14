First public release of NINA Field Kit, for NINA 3.2.0.9001 or newer.

- Alpaca safety monitor combining all enabled sources into one NINA safety result.
- Default 30-second background checks, three attempts per check, two tolerated missed checks, and a 90-second safe-evidence age limit.
- Configurable unsafe confirmation and return-to-safe behavior; HTTP connections renewed within 30 minutes.
- Starfront-compatible responses, themed setup, live configuration edits, retry status, and NINA logging/export diagnostics.
- Read-only Capture Equipment Snapshot and Mount Health Check Advanced Sequencer actions.

Both DLLs are timestamped and signed by StackFoundry LLC using Azure signing. The archive contains the DLLs at its root, README, and Apache-2.0 license. The included NINA manifest and SHA256SUMS.txt identify the final signed archive.

Install **NINA Field Kit** through the plugin source **https://nina-plugins.psf-guard.com/**, then restart NINA. Open Safety Monitor equipment, select **Field Kit Alpaca Safety Monitor**, and open Setup. See the [usage guide](https://github.com/theatrus/nina-field-kit#readme) for Starfront setup, retries, and safety behavior.

109 automated tests cover safety state, retries, freshness expiry, live edits, HTTP behavior, plugin composition, and themed dialogs. The plugin reports safety; configure and verify NINA's sequence response separately. Mount recovery actions are not included.
