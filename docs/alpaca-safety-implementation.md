# Alpaca safety implementation

The plugin exports **Field Kit Alpaca Safety Monitor** through NINA 3.2.0.9001's `IEquipmentProvider` contract. Select it under Safety Monitor equipment, open Setup, add the required sources, review their policies, save settings, and connect from NINA. Later edits can be saved while connected. Every enabled source is required; there is no voting quorum.

## Background checks and connection renewal

`IsSafe` is always a local read. Independent background workers poll each source every **30 seconds** by default. The configurable maximum safe-evidence age is **90 seconds** from the start of the last valid safe request. Retries, pending unsafe readings, and getters never refresh that timestamp. The getter checks monotonic age on every access, and a 250 ms expiry worker updates the UI even while HTTP is waiting.

Retained safe evidence tolerates communication hiccups only while previously established safety remains valid. The default failed-cycle threshold is 3, with 3 attempts per cycle. The default unsafe-reading threshold remains 1; set it higher (for example 3) to dampen a noisy source's safe/unsafe oscillation. Return-to-safe requires both 3 valid safe readings and a 10-second sustained-safe hold. Any failed attempt resets recovery progress, even if its retry succeeds. These thresholds can withdraw safety before the cache lifetime ends.

HTTP sockets renew every **30 minutes** by default. Each source can shorten `ConnectionLifetimeSeconds` to 1–1800 seconds; values above 1800 are rejected. `SocketsHttpHandler.PooledConnectionLifetime` prevents reuse after that age, and the idle lifetime is at most 30 seconds. Renewal happens at request boundaries: an active request still has its own timeout. No logical Alpaca disconnect is sent. This follows the [.NET HttpClient connection-lifetime guidance](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines).

## Components and lifecycle

- Core: immutable configuration snapshots, endpoint state machine, bounded retry backoff, Alpaca protocol adapter, and aggregation service. No NINA or WPF dependency.
- Plugin: one shared provider/device, versioned profile settings, setup and endpoint editor, live diagnostics, independent test history, and redacted JSON diagnostic export.
- Tests: fake monotonic clock, injected HTTP handlers, real loopback HTTP/1.1 server with persistent sockets, profile/MEF checks, and WPF rendering.

Configuration schema 1 has a revision GUID and up to 16 explicitly configured sources. Disabled sources remain visible. Duplicate normalized base-URL/device pairs, empty enabled sets, URL credentials/queries/fragments, invalid counts/timings, unsupported credential references, and incompatible freshness intervals are rejected. Apply creates a new revision. Draft edits are permitted while connected. Saving applies a new configuration while connected; an open dialog cannot apply to a different profile. Standalone source tests remain limited to disconnected mode.

Connect starts a fresh generation with empty safety history. Connected means the local proxy is running, even if every upstream is unreachable. Startup is unsafe. Disconnect immediately detaches the service and makes the device unsafe, then cancels workers and disposes HTTP resources. Profile changes, system suspend/resume, and plugin teardown disconnect the proxy and clear its history. After resume, reconnect from NINA; old safety is never automatically restored.

Each endpoint has one poll loop; four HTTP attempts across endpoints may run concurrently. Retries release the global slot while waiting. Replies from cancelled/retired generations never update live state. A timed-out HTTP operation that has not completed prevents that client from sending another overlapping request. Hard expiry remains independent of these workers. No upstream disconnect is sent on shutdown or as a repair, because connection state may be shared with other clients.

## Protocol and timing

The adapter uses the standard `api/v1/safetymonitor/{deviceNumber}` routes under the configured HTTP/HTTPS base prefix. GETs carry client identity in the query; PUTs use form encoding. Interface versions 1–3 are supported. ExternallyManaged mode performs no writes. Managed mode uses Connected for legacy interfaces and Connect/Connecting/Connected completion checks for interface 3. An uncompleted connection is a retryable result within the normal attempt budget. No newer draft API is assumed. See the [ASCOM SafetyMonitor interface](https://ascom-standards.org/library/html/T_ASCOM_Common_DeviceInterfaces_ISafetyMonitorV3.htm).

Every request has a timeout (default 1 second) covering headers and body. Responses are limited to 64 KiB, headers to 16 KiB, and JSON depth to 16. The adapter rejects duplicate envelope keys, incorrect transaction echoes, missing transaction/value fields, invalid field types, nonzero Alpaca errors, nonempty error messages, and non-Boolean safety values. Omitted ErrorNumber and ErrorMessage use ASCOM Library's success defaults (zero and empty string); explicit nulls remain invalid. HTTP status is classified before parsing a body, so a 502 HTML page is transient rather than a malformed success. Redirects, cookies, and HTTP caching are disabled; normal TLS certificate validation remains enabled.

Retryable statuses are 408, 429, 500, 502, 503, and 504. Timeouts, connection failures, truncated streams, and temporary name resolution failures are transient. Authentication, certificate, permanent name-resolution, route, unsupported-interface, and malformed-success errors are permanent. Unclassified errors fail unsafe. Recognized Alpaca not-connected errors permit bounded reconnection; other Alpaca errors fail immediately. Raw server messages and exception text are omitted from diagnostics because they can echo secrets; numeric error/transaction IDs and a categorized reason are preserved.

Equal-jitter backoff starts at 500 ms, doubles up to 30 seconds, and carries across exhausted cycles. Retry-After on 429/503 is a minimum delay and may exceed that cap. Long waits are sliced into cancellable intervals while cache expiry continues independently. Permanent faults receive low-rate probes at the backoff cap. A valid unsafe observation ends its cycle without retries; only fully exhausted transient cycles increment the failed-cycle count.

## Setup and diagnostics

The setup dialog separates Sources from Live diagnostics. Add/Edit has three tabs: Source (name, URL, device number, enabled switch, whether to send Alpaca Connect commands), Safety behavior (server check interval, missed checks tolerated and unsafe/recovery thresholds), and Advanced (hard age limit, timeout, HTTP renewal, with retry tuning collapsed). A policy summary updates with edits. Add, Edit, Remove and Enable/Disable change only a draft, even while monitoring is active. Save to profile applies the draft without disconnecting. The last reported state is retained during handover; saving does not force an unsafe transition. Test source remains disabled while connected because it uses a separate client. An unsaved-change notice explains the two-stage save. Selection is retained after replacement and moves to the next source after removal. Defaults and existing profile values are preserved. Both windows resolve NINA's live BackgroundBrush and PrimaryBrush and inherit its control styles. Button content, selected tab headers, and grid cells explicitly use matching NINA foreground brushes; the enabled switch has a separate label because NINA's checkbox template does not render Content.

Test uses a fresh client and the selected connection policy; the adjacent help discloses that managed mode may connect the device. It never seeds the live cache. The test display includes interface version, connection status, IsSafe, request latency, error category, and server transaction ID. Server-provided name/description enrichment, discovery, and credential-store integrations remain future work.

The live panel shows every enabled source's raw value, phase, effective permission, safe age, confirmation counts, hold progress, latest poll outcome/age/latency, total polls and failed attempts, attempt number, retry countdown, interface version, upstream connection state, transaction/error numbers, and reason. Latest-attempt metadata is separate from the last valid safety observation, so an in-cycle retry failure cannot misleadingly show an older successful request as the latest poll. HTTP pool lifetime is shown as a policy, not an observed socket-renewal counter.

NINA log entries use the searchable `FieldKitSafety` prefix and include UTC timestamps, named events and structured evidence. Source and aggregate transitions, startup policies, disconnects, profile/power changes, configuration saves and manual tests are logged normally. Unsafe/stale/fault/grace transitions use Warning, internal/configuration/test failures use Error, and normal lifecycle/recovery events use Info. Identical healthy polls are quiet by default. Source events carry endpoint IDs and runtime events carry configuration revision and generation IDs for correlation.

Retry scheduling, exhausted checks, and recovery after retry are logged normally, including attempt counts and delays. **Trace polls for 5 minutes** additionally enables every poll result. Tracing is session-only, expires using elapsed time, can be stopped early, and resets on profile change. It intentionally writes at Info so users need not change NINA's global logging level. Trace does not alter polling, cache, thresholds or safety evaluation. It does not capture HTTP bodies or claim to observe actual socket rotation.

**Recent events** retains the newest 300 events across dialog close/reopen during the plugin session, including manual test results. Entries are bounded and escaped to prevent extra log lines. **Log snapshot** writes the current state into NINA's log. **Copy report** and **Export diagnostics** include versions, current snapshot, redacted policy values and recent events. Endpoint URLs, credentials, HTTP headers and raw response bodies are omitted; user-entered source labels and device numbers remain visible. A diagnostic sink failure cannot authorize or preserve safety.

## Acceptance status

The [Starfront live test](starfront-live-test.md) now passes protocol validation on the documented Building 4 example. The client matches ASCOM Library's defaults for omitted error fields while still requiring a valid Boolean safety value. Three follow-up observations reported unsafe. A read-only probe under `tools/Nina.FieldKit.Probe` reproduces the test without changing NINA settings.

Automated tests exercise protocol failures, state transitions, cache expiry during hung requests, configurable renewal over real persistent TCP sockets, background retry/unsafe behavior, profile persistence/change, MEF discovery, and WPF rendering. Rendered setup previews are written under the ignored `artifacts/` directory during tests.

Installed-host acceptance remains required: verify chooser discovery, profile switching, setup layout in the NINA theme, system suspend/resume, actual unsafe-trigger handling, and sequence reaction latency using a controlled Alpaca simulator. The diagnostic/mount actions remain independent. This plugin reports safety; it does not issue park, roof, tracking, or acquisition commands. No observatory hardware was exercised by the automated tests.




Normal polling waits the configured interval after the previous cycle completes; NINA getters and the 0.5-second diagnostics screen refresh do not send HTTP requests. Diagnostics identify both rates. Transient failures may retry sooner according to backoff. Automated tests use an isolated temporary NINA log directory so synthetic events are not mixed with the user's NINA logs.


## Applying settings while connected

A live save validates and constructs the replacement first, persists a new revision, retires the old workers, and starts replacement polling only after the retired workers finish. NINA remains connected. The last unsafe result stays unsafe until ordinary recovery succeeds. The last safe result can be retained while the replacement obtains fresh evidence, bounded by the earliest original source expiry deadline. Repeated saves preserve that same deadline. Fresh confirmed unsafe evidence, a permanent fault, or expiry withdraws safety; confirmed fresh safety takes over normally. No upstream disconnect is sent. Explicit disconnect, profile changes and suspend/resume still clear evidence immediately.

The UI summary identifies retained results and their remaining lifetime; JSON snapshots expose RetainingPreviousResult and RetainedSafeSecondsRemaining. Live saves also emit ConfigurationAppliedLive. Tests cover repeated edits without extending stale safety, true/false handover, and active-dialog saves.

### Retry visibility

Setup’s main SAFE/UNSAFE status line includes source retry activity: retry countdown and attempt number, exhausted check, or recovery after retry. Missed checks count only exhausted checks, never individual retries. The summary stays visible on every tab; brief retries remain in Recent events and the normal NINA log (`FieldKitSafety`) as `RetryScheduled`, `CheckMissed`, and `CheckRecovered`. Five-minute tracing adds every request result. This presentation does not change polling or safety decisions.
