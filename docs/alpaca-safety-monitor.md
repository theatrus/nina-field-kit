# Field Kit Alpaca Safety Monitor

Status: design proposal, September 13, 2026. No implementation exists yet.

## Purpose and scope

Add a NINA safety-monitor device supplied by Field Kit. It reads one or more configured Alpaca SafetyMonitor devices and presents a single result to NINA. It tolerates brief communication failures without treating an explicit unsafe reading as a network problem.

This complements the mount actions in the [main design](design.md). The first version aggregates external safety endpoints only. Mount health and imaging intent remain separate; a mount recovery fault must not silently become a weather shutdown condition.

Here, proxy means an in-process NINA device that acts as an Alpaca client. It does not publish an Alpaca server or replace the upstream devices. Other applications continue to connect to those devices directly. An externally accessible Alpaca server is outside this design.

The user selects **Field Kit Alpaca Safety Monitor** in NINA's Safety Monitor equipment list. Field Kit occupies that selected-device slot and aggregates the configured endpoints internally. It does not depend on adding several selected safety monitors to NINA.

## Behavior

- Every enabled endpoint is required. All must permit safe operation.
- An explicit valid unsafe response makes the aggregate unsafe immediately upon receipt.
- A transient transport failure may retain the last valid safe result for a bounded, configured interval.
- Startup, expired data, malformed responses, and permanent configuration errors are unsafe.
- No configured endpoints, or no enabled endpoints, is unsafe and a configuration error.
- One responsive endpoint cannot hide another endpoint's unsafe or expired state.
- Field Kit reports state; NINA's sequence decides how to stop or resume operations.

The grace interval trades faster shutdown for fewer false alarms during communication hiccups. It is visible in configuration and diagnostics, and never extends indefinitely through retries.

## Endpoint configuration

Store configuration per NINA profile with a versioned schema. Each endpoint has:

| Setting | Purpose |
| --- | --- |
| Stable ID and label | Identify an endpoint across edits and logs. |
| Enabled | Explicitly include or exclude this source. Exclusion is visible. |
| Base URL | Scheme, host, port, and optional reverse-proxy prefix. Support explicit HTTP and HTTPS. |
| Device number | Nonnegative Alpaca SafetyMonitor device number. |
| Connection policy | Manage this client connection, or read an externally managed connection. |
| Poll interval | Time between ordinary polls. |
| Request timeout | Bound each HTTP operation. |
| Maximum safe age | Maximum age of a valid safe observation before it ceases to authorize safe operation. |
| Retry policy | Bounded transient retries with backoff and jitter. |
| Credential reference | Optional reference to a supported credential store; never embed secrets in exported settings. |

Provide Add, Edit, Remove, Enable, Test, and optional Discover controls. Manual configuration is required; discovery is a convenience. Discovery must not silently add, replace, or enable sources. Duplicate normalized URL/device pairs are rejected. Redundant routes to the same physical sensor are not independent safety sources.

Test displays server/device identity where available, interface support, connection status, IsSafe, latency, and errors. A test result does not seed the live safety cache. Any test that connects must disclose that effect and follow the chosen connection policy.

Edits create an immutable configuration revision. The first release permits applying changes only while the proxy is disconnected. Reconnecting starts unsafe with an empty cache. This prevents an unnoticed endpoint deletion or URL change from making a running system safe. Export contains no cached safe readings or secrets.

## Initial timing defaults

Proposed defaults for validation, not guarantees about every observatory:

| Setting | Default |
| --- | --- |
| Poll interval | 2 seconds |
| Request timeout | 1 second |
| Retry | One extra attempt after approximately 250 ms with jitter |
| Maximum safe age | 10 seconds, measured from the start of the last successful safe request |
| Return-to-safe hold | 10 seconds of sustained fresh safe results, with at least two successful polls per endpoint |

The setup screen must show these values before first use. Offer a fail-on-first-error mode that disables retention after a failed request. Reject timing settings whose routine polling cannot satisfy their own freshness limits. Retry and reconnect work share endpoint deadlines and cannot reset safe age. The UI shows an estimated worst-case explicit-unsafe detection delay from polling and timeout settings, plus NINA's own polling/trigger latency.

## State model

Retain the last valid Boolean separately from communication state. Record request start, receipt time, last successful request start, error category, consecutive failures, and active configuration revision. Use monotonic time for durations and UTC timestamps for logs.

| Endpoint state | Meaning | Permits aggregate safe? |
| --- | --- | --- |
| Unknown | No valid result in this connection generation. | No |
| FreshSafe | Latest valid observation is safe and within its age limit. | Yes, after recovery hold |
| GraceSafe | A transient failure followed a safe result that has not expired. | Yes only if the aggregate was already safe |
| Unsafe | Latest valid observation is unsafe. | No |
| Stale | The safe result has expired, including when polling has stalled. | No |
| Faulted | Invalid payload, authentication/configuration error, or unsupported protocol. | No |

If an endpoint reports unsafe, discard its eligibility for grace immediately. A later timeout must never revive an older safe result. Fresh safe observations are needed to recover.

Aggregate safety is the conjunction of enabled endpoints' permission states. Startup and every unsafe-to-safe transition require the return-to-safe hold. During that hold, every endpoint must remain fresh and successful polls must continue; a failed poll resets the hold. Grace preserves an existing safe state, but cannot establish a new one. Any valid unsafe reading bypasses all holds and grace.

Example: an endpoint's safe request starts at t=0 and succeeds. If subsequent reads time out, that result expires at t=10, not ten seconds after the last retry. A valid unsafe response at t=3 makes the result unsafe at t=3. If the last valid reading was unsafe, there is no grace at all.

Do not persist grace across disconnect, restart, profile change, endpoint change, or system resume. Invalidate cached safety after sleep/resume; elapsed time and scheduling must not preserve stale authorization.

## Alpaca client behavior

Read IsSafe through the standard route, for example:

```
GET {base}/api/v1/safetymonitor/{deviceNumber}/issafe
```

Supply ClientID and ClientTransactionID according to the Alpaca API. GET parameters belong in the query; PUT parameters use form encoding. An HTTP 200 alone is not success: validate the response envelope, ErrorNumber, and the Boolean Value. Preserve server transaction IDs and error details for diagnosis. Do not coerce missing values or strings into true.

Use bounded response sizes and validate the expected response type. Disable HTTP caching. Reject unexpected redirects by default, especially across hosts, to avoid changing device identity or forwarding credentials. HTTPS certificate validation stays enabled. Authentication support is an explicit feature; unsupported schemes fail clearly rather than retrying forever.

Negotiate supported connection behavior against the chosen interface version. For legacy devices, managed mode may use Connected; newer connection methods require their defined completion checks. Read-only connection mode never issues connect/disconnect writes. The implementation must follow the published interface rather than assuming all generations have identical semantics.

A transient timeout, connection reset, temporary DNS failure, or temporary server unavailability is eligible for bounded retry and grace. Authentication failures, malformed payloads, wrong devices, unsupported methods, and non-transient Alpaca errors fail unsafe immediately. A recognized not-connected response may initiate managed reconnection, but does not refresh the safe reading. Unknown errors fail unsafe until explicitly classified by tests.

Reconnect at a bounded rate. Never disconnect another client as an attempted repair. Some servers share connection state across clients; do not automatically send upstream disconnect on proxy shutdown in the first release. Stop local workers and release HTTP resources. Document the connection policy and shared-client behavior.

Use a tested ASCOM Alpaca client library if its retries, timeouts, connection behavior, and errors can satisfy these rules. Otherwise use a narrow HTTP adapter. Hidden library retries must not outlive the freshness budget. The existing HTTP connection pool may reconnect transport sockets without changing the logical device connection.

## NINA integration and failure handling

Implement NINA's ISafetyMonitor/IDevice contract and export a safety-monitor equipment provider. The NINA 3.2 chooser already enumerates plugin providers. Verify exact provider registration and lifecycle requirements during implementation.

Connect starts the aggregation service. Connected describes the local proxy service lifecycle, not unanimous upstream reachability. Upstream failures normally leave the proxy connected but unsafe, so NINA receives a stable device with a meaningful Boolean. Connecting does not imply safe. A startup UI should distinguish connected/waiting-for-data from safe.

IsSafe is a fast local read, with no blocking network requests. On every read, reevaluate age limits against the clock; never return a cached aggregate true after its inputs expire. A separate expiry scheduler updates the UI even when no polls complete. If polling workers stop or throw, the result expires unsafe. An unrecoverable service fault returns unsafe and surfaces the fault; it never freezes the last true value.

Each endpoint has one active poll/reconnect operation at a time. Poll endpoints independently with a bounded global concurrency limit, so one slow endpoint does not prevent receipt of another's unsafe result. Tag responses with configuration revision, connection generation, and request identity. Discard late replies after timeout, cancellation, disconnect, or revision change. A slow safe response cannot overwrite a newer unsafe result.

Cancellation must stop scheduling and prevent stale callbacks from mutating state. Disconnect immediately makes the local state unavailable/unsafe and clears safety history. NINA can still react to the disconnected state through its own safety logic.

## UI and diagnostics

Display aggregate Safe, Unsafe, or Safe using recent data, alongside each endpoint's last reported value, observation age, connection state, next retry, and reason. The degraded-safe presentation must be distinct even though NINA's public contract remains Boolean.

Log state transitions, not every identical poll at information level. Include endpoint ID, configuration revision, effective timing policy, transition reason, request latency, age, and error category. Use debug logs for individual requests. Do not log authorization headers or credentials in URLs. Provide a redacted diagnostic export and a test history.

Show the main cause of unsafe and all other failing sources. Avoid a generic “not connected” label when the source explicitly reports weather unsafe. Do not invent weather reasons: legacy IsSafe may supply only a Boolean.

## Tests and acceptance criteria

Use a fake clock and local fake Alpaca servers for repeatable tests:

- Zero enabled endpoints, startup, restart, profile change, and resume are unsafe until validated.
- All sources safe produces safe only after the hold.
- One explicit unsafe response immediately makes the aggregate unsafe.
- Brief transport errors preserve existing safe state only within the configured age.
- Exact expiry while a request hangs returns unsafe, even if the poll worker is stuck.
- An unsafe result followed by transport errors remains unsafe.
- Recovery hold resets on failed polls; grace cannot create safe state.
- HTTP 200 with Alpaca ErrorNumber nonzero, invalid JSON, non-Boolean Value, and authentication failure never produce safe.
- A slow endpoint does not delay a second endpoint's unsafe result.
- Late safe responses cannot undo unsafe, disconnect, or a configuration revision change.
- Wall-clock adjustments do not extend grace; suspend/resume clears it.
- Managed and externally managed connection modes respect shared clients.
- Secrets remain absent from logs and exported settings.

Windows/NINA tests must verify provider discovery, profile persistence, connect/disconnect, responsive UI, and actual unsafe trigger behavior. Induce a brief network interruption and a longer outage while observing the sequence. Verify an explicit unsafe value is never delayed by the transport grace policy. Record NINA's polling and action latency separately from proxy latency.

## Delivery

1. Single-endpoint provider, protocol adapter, strict validation, and diagnostic UI.
2. Multiple endpoints, all-required aggregation, and immutable profile settings.
3. Configurable freshness grace, recovery hold, and fault-injection tests.
4. Windows integration and observed sequence tests before release.
5. Optional discovery, credential schemes, and later diagnostics supported by newer interfaces.

Do not depend on the draft SafetyMonitor V4 API. Richer reasons may be added after supported versions and semantics are verified. Quorum voting, redundant sensor groups, local mount health aggregation, and external Alpaca serving are separate future designs.

## References

- [ASCOM Alpaca API](https://ascom-standards.org/api/) — routes, transactions, encoding, and errors.
- [ASCOM SafetyMonitor client documentation](https://www.ascom-standards.org/alpyca/alpaca.safetymonitor.html) — safety property and device contract.
- [NINA 3.2 safety-monitor interface](https://github.com/isbeorn/nina/blob/release/3.2.x/NINA.Equipment/Interfaces/ISafetyMonitor.cs).
- [NINA 3.2 safety-monitor chooser](https://github.com/isbeorn/nina/blob/release/3.2.x/NINA.WPF.Base/ViewModel/Equipment/SafetyMonitor/SafetyMonitorChooserVM.cs) — plugin provider integration.
