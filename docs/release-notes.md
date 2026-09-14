NINA Field Kit 0.1.0.5, for NINA 3.2.0.9001 or newer.

All Alpaca HTTP requests now identify the plugin with `User-Agent: NINA-Field-Kit/0.1.0.5`, including safety checks and connection requests. The version follows the installed plugin release.

This release also hardens response handling: invalid UTF-8 is rejected, and malformed HTTP body framing is distinguished from temporary connection interruptions. A new fault-server integration suite exercises errors, malformed data, latency, retries, recovery, and connection renewal.

Validation includes 191 automated tests and a real-time test of the default polling, freshness, and recovery policy. Release builds require both checks to pass.

Published DLLs are code-signed. The archive includes the plugin DLLs, README, and Apache-2.0 license. The manifest and SHA256SUMS.txt identify the release archive.

Install or update **NINA Field Kit** through the plugin source **https://nina-plugins.psf-guard.com/**, then restart NINA. Open Safety Monitor equipment, select **Field Kit Alpaca Safety Monitor**, and open Setup. See the [usage guide](https://github.com/theatrus/nina-field-kit#readme) for configuration and safety behavior.
