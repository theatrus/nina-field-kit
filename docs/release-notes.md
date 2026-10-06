Field Kit 0.1.0.12, for NINA 3.2.0.9001 or newer.

Safety monitor requests now explicitly send **Connection: keep-alive**. HTTP connections continue to use the existing pool, with a 30-second idle timeout and a configurable maximum reuse age of up to 30 minutes.

TCP keepalive probes remain at framework defaults, matching the ASCOM Alpaca client. Polling, retries, request timeouts, and safety decisions are unchanged.

Connection reuse was verified against a live Alpaca endpoint, including polls spaced 30 seconds apart. Release validation runs the automated test suite and the real-time default safety-policy acceptance check.

Published DLLs are code-signed. Update **Field Kit** through **https://nina-plugins.psf-guard.com/** and restart NINA.
