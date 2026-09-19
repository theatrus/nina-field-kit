Field Kit 0.1.0.10, for NINA 3.2.0.9001 or newer.

Safety Monitor Setup now includes an immediate **Output override** with three choices: **Safe**, **Passthrough**, and **Unsafe**. Use it while adjusting source settings without changing the output sent to NINA. Safe forces a safe output even when sources are unsafe or unavailable; Unsafe forces unsafe; Passthrough uses the current combined source result.

Background polling and recovery tracking continue in every mode. The status line identifies active overrides and the actual source result. Changes are recorded in the NINA log and diagnostic reports.

The override is session-only: closing Setup or saving settings retains it, while disconnecting, changing profiles, suspending/resuming, or restarting resets it to Passthrough. Return to Passthrough when finished adjusting settings.

Published DLLs are code-signed. Release validation includes the automated test suite and the real-time default safety-policy acceptance check.

Update **Field Kit** through **https://nina-plugins.psf-guard.com/** and restart NINA.
