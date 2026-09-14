NINA Field Kit 0.1.0.4, for NINA 3.2.0.9001 or newer.

This patch replaces provider-specific names, addresses, and setup guidance with generic Alpaca examples. Polling, retry, and safety behavior are unchanged.

- Alpaca safety monitor combining all enabled sources into one NINA safety result.
- Default 30-second background checks, three attempts per check, two tolerated missed checks, and a 90-second safe-evidence age limit.
- Configurable unsafe confirmation and return-to-safe behavior; HTTP connections renewed within 30 minutes.
- ASCOM-compatible response parsing, themed setup, live configuration edits, retry status, and NINA logging/export diagnostics.

Both DLLs are code-signed. The archive contains the DLLs at its root, README, and Apache-2.0 license. The included NINA manifest and SHA256SUMS.txt identify the final signed archive.

Install **NINA Field Kit** through the plugin source **https://nina-plugins.psf-guard.com/**, then restart NINA. Open Safety Monitor equipment, select **Field Kit Alpaca Safety Monitor**, and open Setup. See the [usage guide](https://github.com/theatrus/nina-field-kit#readme) for source setup, retries, and safety behavior.

109 automated tests cover safety state, retries, freshness expiry, live edits, HTTP behavior, plugin composition, and themed dialogs. The plugin reports safety; configure and verify NINA's sequence response separately.
