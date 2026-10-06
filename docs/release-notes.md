Field Kit 0.1.0.13, for NINA 3.2.0.9001 or newer.

Safety monitor HTTP connections now have a **60-second pool idle timeout**, twice the default 30-second poll interval. This gives connections room to stay available between normal polls. A shorter configured maximum connection lifetime also caps idle expiry.

Requests continue to send **Connection: keep-alive**, and connection reuse remains limited to at most 30 minutes. Polling, retries, request timeouts, and safety decisions are unchanged.

Published DLLs are code-signed. Release validation runs the automated test suite and the real-time default safety-policy acceptance check.

Update **Field Kit** through **https://nina-plugins.psf-guard.com/** and restart NINA.
