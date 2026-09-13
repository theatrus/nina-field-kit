# Field Kit Alpaca Safety Monitor

Status: design proposal, September 13, 2026. No implementation exists yet.

## Purpose and scope

Add a NINA safety-monitor device supplied by Field Kit. It reads one or more configured Alpaca SafetyMonitor devices and presents a single result to NINA. It tolerates brief communication failures without treating an explicit unsafe reading as a network problem.

This complements the mount actions in the [main design](design.md). The first version aggregates external safety endpoints only. Mount health and imaging intent remain separate; a mount recovery fault must not silently become a weather shutdown condition.

Here, proxy means an in-process NINA device that acts as an Alpaca client. It does not publish an Alpaca server or replace the upstream devices. Other applications continue to connect to those devices directly. An externally accessible Alpaca server is outside this design.

The user selects **Field Kit Alpaca Safety Monitor** in NINA's Safety Monitor equipment list. Field Kit occupies that selected-device slot and aggregates the configured endpoints internally. It does not depend on adding several selected safety monitors to NINA.

## Behavior

- Every enabled endpoint is required. All must permit safe operation.
- Valid unsafe readings and transport failures have separate configurable confirmation thresholds. An unsafe reading is never retried as though it were a network error.
- A transient transport failure may retain the last valid safe result for a bounded, configured interval.
- Startup, expired data, malformed responses, and permanent configuration errors are unsafe.
- No configured endpoints, or no enabled endpoints, is unsafe and a configuration error.
- One responsive endpoint cannot hide another endpoint's confirmed unsafe or expired state.
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
| Failed cycles to unsafe | Consecutive failed poll cycles required to withdraw safety; a cycle includes its retries. |
| Unsafe readings to unsafe | Consecutive valid unsafe poll results required to withdraw safety. |
| Safe readings to safe | Consecutive valid safe poll results required to establish or restore safety. |
| Return-to-safe hold | Minimum sustained-safe duration, in addition to the safe-reading count. |
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
| Attempts per poll cycle | 3 total, including the first request |
| Retry backoff | 500 ms initial, multiplier 2, 30-second cap, equal jitter |
| Failed cycles to unsafe | 3 |
| Unsafe readings to unsafe | 1; configurable, for example 3 for a noisy source |
| Safe readings to safe | 3 |
| Maximum safe age | 10 seconds, measured from the start of the last successful safe request |
| Return-to-safe hold | 10 seconds of sustained fresh safe results, plus the configured safe-reading count |

The setup screen must show these values before first use. Fail-on-first-error sets attempts per cycle and failed-cycle threshold to 1; immediate-unsafe sets the unsafe-reading threshold to 1. Counts must be positive integers. Reject timing settings whose routine polling cannot satisfy their own freshness limits. Retry and reconnect work cannot reset safe age. The age limit is an independent hard bound: it may make the endpoint unsafe before a configured count is reached. Show this interaction and the nominal confirmation delay in the UI, separately from NINA's polling/trigger latency.

## State model

Retain the last valid Boolean separately from communication state. Record request start, receipt time, last successful request start, error category, consecutive failures, and active configuration revision. Use monotonic time for durations and UTC timestamps for logs.

| Endpoint state | Meaning | Permits aggregate safe? |
| --- | --- | --- |
| Unknown | No valid result in this connection generation. | No |
| FreshSafe | Latest valid observation is safe and within its age limit. | Yes, after recovery hold |
| GraceSafe | A transient failure followed a safe result that has not expired. | Yes only if the aggregate was already safe |
| PendingUnsafe | Unsafe readings have not yet reached their threshold. | Only preserves existing safety while the prior safe result remains within its age limit |
| PendingSafe | Safe readings or duration have not yet reached their recovery thresholds. | No |
| Unsafe | An unsafe-reading or failed-cycle threshold was reached. | No |
| Stale | The safe result has expired, including when polling has stalled. | No |
| Faulted | Invalid payload, authentication/configuration error, or unsupported protocol. | No |

Track three independent counters: consecutive failed cycles, consecutive valid unsafe observations, and consecutive valid safe observations. A valid response, whether safe or unsafe, resets failed cycles and transport backoff. A valid safe response clears the unsafe counter; a valid unsafe response clears the safe counter and recovery hold. A failed cycle clears the safe counter and hold but preserves pending unsafe evidence: it is not evidence of safe conditions. Here consecutive unsafe means successive valid observations; intervening failed cycles neither count as unsafe readings nor erase them. Count safe/unsafe observations once per successful poll cycle, never once per NINA getter call.

Withdraw endpoint safety on the first of: unsafe-reading threshold, failed-cycle threshold, maximum safe age, or a permanent fault. Pending unsafe readings do not refresh safe age. Once unsafe, neither a retry nor an old safe sample can restore safety. Require the safe-reading count AND the return-to-safe hold; safe results must stay fresh and failures reset recovery progress. Any failed attempt, even one followed by a successful retry, resets recovery progress; that successful result can begin a new safe run.

Aggregate safety is the conjunction of enabled endpoints' confirmed permission states. Each endpoint confirms its own transitions; do not add a second hidden aggregate debounce. Pending states and grace can preserve an already-safe aggregate but cannot establish safe at startup or restore an unsafe aggregate. Restoration requires every enabled endpoint to be FreshSafe with recovery confirmed.

Example: with unsafe threshold 3, safe at t=0 followed by unsafe at t=2 and t=4 preserves the existing safe output as PendingUnsafe. Unsafe at t=6 makes it unsafe. A safe result at t=4 instead clears the pending unsafe count. With a ten-second maximum safe age, insufficient further responses still force unsafe at t=10 even if the count never reaches 3. After confirmed unsafe, three safe readings alone do not restore safe if the ten-second recovery hold has not elapsed.

These thresholds count repeated observations from each endpoint. They are not votes across endpoints: requiring two different weather sources to declare unsafe would be a separate aggregation policy.

Do not persist grace across disconnect, restart, profile change, endpoint change, or system resume. Invalidate cached safety after sleep/resume; elapsed time and scheduling must not preserve stale authorization.

## Alpaca client behavior

Read IsSafe through the standard route, for example:

```
GET {base}/api/v1/safetymonitor/{deviceNumber}/issafe
```

Supply ClientID and ClientTransactionID according to the Alpaca API. GET parameters belong in the query; PUT parameters use form encoding. An HTTP 200 alone is not success: validate the response envelope, ErrorNumber, and the Boolean Value. Preserve server transaction IDs and error details for diagnosis. Do not coerce missing values or strings into true.

Use bounded response sizes and validate the expected response type. Disable HTTP caching. Reject unexpected redirects by default, especially across hosts, to avoid changing device identity or forwarding credentials. HTTPS certificate validation stays enabled. Authentication support is an explicit feature; unsupported schemes fail clearly rather than retrying forever.

Negotiate supported connection behavior against the chosen interface version. For legacy devices, managed mode may use Connected; newer connection methods require their defined completion checks. Read-only connection mode never issues connect/disconnect writes. The implementation must follow the published interface rather than assuming all generations have identical semantics.

A transient timeout, connection reset, temporary DNS failure, or temporary server unavailability is eligible for retry, backoff, and the failed-cycle threshold. HTTP 408, 429, 500, 502, 503, and 504 are retryable by default. A 502 HTML error page is classified by its HTTP status, not as a malformed Alpaca success payload. Authentication failures (401/403), wrong routes/devices (404), invalid TLS certificates, malformed successful responses, unsupported methods, and non-transient Alpaca errors fail unsafe immediately. A recognized not-connected response may initiate managed reconnection under the same bounded cycle, but does not refresh the safe reading. Unknown errors fail unsafe until explicitly classified by tests. Persist any future classification overrides in the configuration and show them in diagnostics.

### Retry and backoff scheduling

A poll cycle ends with one valid observation, a permanent fault, or exhaustion of its attempt budget. Each exhausted transient cycle increments failed cycles once; three failed HTTP attempts within that cycle do not count as three failed cycles. A valid IsSafe false ends the cycle successfully at the transport layer and increments the unsafe-reading counter. It does not cause immediate transport retries to manufacture more unsafe samples.

For successive transient errors, calculate B = min(cap, initialDelay * multiplier^(errorStreak - 1)) with overflow protection. Equal jitter chooses a delay uniformly between B/2 and B. Carry the error streak across exhausted cycles so an extended outage does not restart rapid retries every poll interval. The next cycle begins after the greater of its backoff delay and the normal poll interval; never overlap cycles. A valid Alpaca observation resets backoff, and normal polling resumes. Permanent faults remain unsafe and receive low-rate probes at the configured backoff cap, so correcting the server can be detected without restarting NINA.

Honor a valid Retry-After on 429/503 as a minimum delay, supporting seconds and HTTP-date values. It can exceed the local backoff cap; freshness expiry still occurs on time while waiting. Invalid values fall back to local backoff. The UI must show unusually long server-requested delays. Recovery probes continue at the bounded rate after the endpoint becomes unsafe. Backoff does not freeze state evaluation or extend grace, and one endpoint's retry schedule does not stall another endpoint.

Example with an already-safe endpoint, failed-cycle threshold 3, and three attempts per cycle: 502, 502, then a valid safe response produces no failed cycle and resets transport backoff. Three cycles that each exhaust all attempts produce three failed cycles and unsafe, unless safe-age expiry has already made it unsafe. A valid unsafe response follows the unsafe-reading policy instead, even after earlier 502 responses.

Reconnect at a bounded rate. Never disconnect another client as an attempted repair. Some servers share connection state across clients; do not automatically send upstream disconnect on proxy shutdown in the first release. Stop local workers and release HTTP resources. Document the connection policy and shared-client behavior.

Use a tested ASCOM Alpaca client library if its retries, timeouts, connection behavior, and errors can satisfy these rules. Otherwise use a narrow HTTP adapter. Hidden library retries must not outlive the freshness budget. The existing HTTP connection pool may reconnect transport sockets without changing the logical device connection.

## NINA integration and failure handling

Implement NINA's ISafetyMonitor/IDevice contract and export a safety-monitor equipment provider. The NINA 3.2 chooser already enumerates plugin providers. Verify exact provider registration and lifecycle requirements during implementation.

Connect starts the aggregation service. Connected describes the local proxy service lifecycle, not unanimous upstream reachability. Upstream failures normally leave the proxy connected but unsafe, so NINA receives a stable device with a meaningful Boolean. Connecting does not imply safe. A startup UI should distinguish connected/waiting-for-data from safe.

IsSafe is a fast local read, with no blocking network requests. On every read, reevaluate age limits against the clock; never return a cached aggregate true after its inputs expire. A separate expiry scheduler updates the UI even when no polls complete. If polling workers stop or throw, the result expires unsafe. An unrecoverable service fault returns unsafe and surfaces the fault; it never freezes the last true value.

Each endpoint has one active poll/reconnect operation at a time. Poll endpoints independently with a bounded global concurrency limit, so one slow endpoint does not prevent receipt of another's unsafe result. Tag responses with configuration revision, connection generation, and request identity. Discard late replies after timeout, cancellation, disconnect, or revision change. A slow safe response cannot overwrite a newer unsafe result.

Cancellation must stop scheduling and prevent stale callbacks from mutating state. Disconnect immediately makes the local state unavailable/unsafe and clears safety history. NINA can still react to the disconnected state through its own safety logic.

## UI and diagnostics

Display aggregate Safe, Unsafe, or Safe pending confirmation, alongside each endpoint's raw reported value, confirmed output, observation age, connection state, next retry, and reason. Show counters such as “unsafe readings 2/3,” “failed cycles 1/3,” and “safe readings 2/3; hold 4/10 seconds,” plus attempts within the current cycle. Pending unsafe and grace must be distinct from fresh safe even though NINA's public contract remains Boolean.

Log state transitions, not every identical poll at information level. Include endpoint ID, configuration revision, effective timing policy, transition reason, request latency, age, and error category. Use debug logs for individual requests. Do not log authorization headers or credentials in URLs. Provide a redacted diagnostic export and a test history.

Show the main cause of unsafe and all other failing sources. Avoid a generic “not connected” label when the source explicitly reports weather unsafe. Do not invent weather reasons: legacy IsSafe may supply only a Boolean.

## Tests and acceptance criteria

Use a fake clock and local fake Alpaca servers for repeatable tests:

- Zero enabled endpoints, startup, restart, profile change, and resume are unsafe until validated.
- All sources safe produces safe only after the hold.
- Unsafe threshold 1 reacts immediately; threshold 3 requires three successive valid unsafe observations, unless age expiry intervenes.
- Safe, unsafe, unsafe, safe clears pending unsafe without a false transition.
- Unsafe, failed cycle, unsafe preserves unsafe evidence without counting the failure as an unsafe reading.
- Recovery requires both safe count and duration; intermittent failures cannot complete the hold.
- 502/503/504, timeouts, and resets back off across attempts and cycles with bounded jitter.
- Retry-After delays requests but never delays safe-age expiry.
- Retry success resets backoff; exhausted attempts count as one failed cycle.
- Explicit unsafe does not trigger transport retries; permanent faults bypass confirmation counts.
- Brief transport errors preserve existing safe state only within the configured age.
- Exact expiry while a request hangs returns unsafe, even if the poll worker is stuck.
- A confirmed unsafe result followed by transport errors remains unsafe; pending unsafe cannot survive past safe-age expiry.
- Recovery hold resets on failed polls; grace cannot create safe state.
- HTTP 200 with Alpaca ErrorNumber nonzero, invalid JSON, non-Boolean Value, and authentication failure never produce safe.
- A slow endpoint does not delay a second endpoint's unsafe result.
- Late safe responses cannot undo unsafe, disconnect, or a configuration revision change.
- Wall-clock adjustments do not extend grace; suspend/resume clears it.
- Managed and externally managed connection modes respect shared clients.
- Secrets remain absent from logs and exported settings.

Windows/NINA tests must verify provider discovery, profile persistence, connect/disconnect, responsive UI, and actual unsafe trigger behavior. Induce intermittent 502 responses, alternating safe/unsafe readings, a brief network interruption, and a longer outage. Verify transport failures and valid unsafe observations follow their separate thresholds and no retry delays the hard expiry. Record NINA's polling and action latency separately from proxy latency.

## Delivery

1. Single-endpoint provider, protocol adapter, strict validation, and diagnostic UI.
2. Multiple endpoints, all-required aggregation, and immutable profile settings.
3. Configurable backoff, failure/unsafe/safe counters, freshness limits, recovery hold, and fault-injection tests.
4. Windows integration and observed sequence tests before release.
5. Optional discovery, credential schemes, and later diagnostics supported by newer interfaces.

Do not depend on the draft SafetyMonitor V4 API. Richer reasons may be added after supported versions and semantics are verified. Quorum voting, redundant sensor groups, local mount health aggregation, and external Alpaca serving are separate future designs.

## References

- [ASCOM Alpaca API](https://ascom-standards.org/api/) — routes, transactions, encoding, and errors.
- [ASCOM SafetyMonitor client documentation](https://www.ascom-standards.org/alpyca/alpaca.safetymonitor.html) — safety property and device contract.
- [NINA 3.2 safety-monitor interface](https://github.com/isbeorn/nina/blob/release/3.2.x/NINA.Equipment/Interfaces/ISafetyMonitor.cs).
- [NINA 3.2 safety-monitor chooser](https://github.com/isbeorn/nina/blob/release/3.2.x/NINA.WPF.Base/ViewModel/Equipment/SafetyMonitor/SafetyMonitorChooserVM.cs) — plugin provider integration.
