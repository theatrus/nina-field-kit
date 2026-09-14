# .NET HTTP failure-handling audit

Scope: the production `AlpacaSafetyClient` and its use by `SafetyMonitorService`, exercised with .NET 8 on Windows. Tests use production sockets where possible and injected exceptions for DNS and error categories that a local HTTP server cannot reliably induce. Runtime tested locally: .NET 8.0.29.

## Confirmed findings and fixes

### Body-read error categories were discarded

`HttpRequestException` was classified using its .NET 8 `HttpRequestError`, but body-read exceptions reached a generic `IOException` catch. A complete HTTP 200 response with invalid chunk framing was therefore treated as a temporary stream interruption.

Fix: catch `HttpIOException` before `IOException`. `ResponseEnded`/`ConnectionError` remain temporary; invalid framing and other classified body errors are permanent. A real-socket regression sends invalid chunk-size bytes and requires permanent failure. A separate truncated-content-length fixture requires temporary failure. This follows the distinction between request and response-read failures described in Microsoft's [.NET 8 networking diagnostics](https://devblogs.microsoft.com/dotnet/dotnet-8-networking-improvements/).

### Invalid UTF-8 in unused JSON data could accompany accepted safety

The new fixture sent valid framing and `Value=true`, plus byte `0xff` inside an unused JSON string property. Before the fix the client returned `Observation`. JSON string decoding can be deferred, and inspecting just the fields used by the client did not validate those unused bytes.

Fix: validate the entire bounded body with a strict UTF-8 decoder before `JsonDocument.Parse`. Invalid encoding produces a sanitized permanent-failure reason. The test first reproduced acceptance and then passed after the fix. The 64 KiB cap is applied before this extra validation; no unlimited text allocation is introduced.

### Requests needed an identifying User-Agent

All requests now include `NINA-Field-Kit/<assembly-version>`. The server asserts the header on both GETs and connection-command PUTs. The version comes from the core assembly; shared build metadata keeps its default version aligned with the plugin, and release builds override the assembly versions together. It includes no endpoint label, device assignment, machine name, or user identity.

## Reviewed failure policy

| Condition | Behavior and evidence |
| --- | --- |
| HTTP 408/429/500/502/503/504 | Temporary; HTML payload does not override status classification. Real-socket matrix covers each status. |
| Other tested HTTP errors and redirects | Permanent; redirect target receives no request. Normal slow probes can later recover. |
| .NET connection/name-resolution/response-ended request errors | Temporary, except explicit `SocketError.HostNotFound`, which retains the existing permanent classification. Temporary DNS `TryAgain` is covered separately. |
| TLS, authentication, protocol/framing, configuration limits, proxy tunnel, version negotiation, unknown request errors | Permanent/fail-closed under current policy. Every .NET 8 enum value has a regression expectation. Unknown does not become a generic transient retry. |
| Classified response-body read errors | Preserve their category; see the fix above. Unclassified plain `IOException` remains temporary. |
| Caller cancellation | Propagates cancellation rather than synthesizing a missed check. The service's retired-generation/token checks prevent late updates. |
| Per-request timeout | Temporary; deadline covers headers and body consumption. Safe data arriving after cancellation does not refresh evidence. |
| Timed-out operation still unwinding | No overlapping HTTP operation is sent. Immediate manual polling can receive the existing temporary "prior operation" result until cleanup completes. This is not a listener-restart failure. |
| Invalid JSON, field types, transactions, UTF-8, duplicates, body/depth limits | Permanent; malformed content never authorizes safety. Boundary tests cover 65,535/65,536/65,537 bytes in fixed-length and chunked responses. |
| Alpaca `0x407` | Temporary, clears preparation, and rechecks version/connection on the next attempt. Other nonzero errors and nonempty error messages fail closed. |
| Retry-After | Seconds/date values honored on 429/503; past dates become zero; malformed values do not replace backoff. Waiting cannot suppress evidence expiry or disconnect. |

## Pooling, cancellation, and state ownership

The client keeps one owned HttpClient/handler per source and limits pooled connection age to the configured maximum of 30 minutes. It also limits idle reuse and per-server connections. It does not recreate HttpClient on every poll. Microsoft's [HttpClient guidance](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines) explains why bounded connection lifetimes allow renewed DNS resolution while avoiding unnecessary per-request pools.

The source worker owns retries; `PollAsync` can perform multiple preparation exchanges in one attempt. The global four-slot semaphore bounds active client operations, and each source has one worker. Real multi-source tests verify the global cap and that disabled sources receive no traffic. A slow source does not monopolize all slots.

Safe evidence is measured from the successful safety request's start. Retry delays, getters, failed requests, and live edits do not refresh it. Cancellation and generation checks prevent retired replies from updating live state. Both exact fake-time tests and real outage/recovery tests cover independent expiry.

Redirects and automatic cookies remain disabled. Normal certificate validation remains enabled; the local self-signed certificate fixture is rejected without changing certificate stores or bypassing validation. HTTP response bodies, error messages, and exception messages are not included in production diagnostics. Synthetic secret-marker tests check the monitor evidence for leakage.

## Harness findings

The new server initially blocked on an empty request-body read; it now skips zero-length body reads. Restarting a listener originally allocated with port zero could choose another port; resume now binds its saved assigned port. These were fixture failures, not changes to the driver's error policy. Tests also allow a timed-out transport to unwind before requiring successful recovery, preserving the client's no-overlap guarantee.

The existing fixture's disposal race fix is preserved. The new server cancels work, stops/joins the accept loop, closes accepted clients, and joins handlers. Unexpected fixture errors fail test cleanup rather than being accepted as the intended HTTP fault.

## Boundaries of this audit

OS DNS changes, enterprise proxy authentication, trusted-success TLS deployment, system suspend/resume, and installed-NINA sequence reactions are not claimed as end-to-end coverage. DNS classification is injected; untrusted TLS and TCP interruption use real sockets. The production client continues to honor platform proxy configuration. The long soak checks renewal and sustained safety, not general memory leak freedom. See [running the fault tests](running-fault-tests.md) for commands and CI lanes.
