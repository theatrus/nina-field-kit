# Alpaca fault-server integration suite

Status: proposed design. The fault server, scenario runner, commands, and control API below are not implemented yet. This work covers the safety monitor only.

## Objective

Run the production safety client and aggregation service against a controllable HTTP server that can return errors, malformed data, slow responses, and broken connections. Reuse that server with an installed NINA plugin to verify the visible result and diagnostics.

A passing test must establish both what the server sent and what the monitor did. An expected unsafe result is a successful test when the scenario requires it. Receiving a particular HTTP status alone is not sufficient.

## Existing coverage and the gap

The current suite has 109 tests. `AlpacaClientTests` injects an `HttpMessageHandler` for protocol parsing and classification. `SafetyServiceTests` uses fake time and clients for retry, freshness, aggregation, and live-edit behavior. `SafetyIntegrationTests` exercises a real loopback server, including 502 recovery, socket renewal, profile changes, and WPF rendering.

`LocalAlpacaServer` currently generates complete JSON envelopes and supports a status/value callback on `issafe`. It cannot reliably script malformed bytes, body stalls, interrupted transfers, or failures during connection setup. The standalone Probe invokes `AlpacaSafetyClient` directly; it does not exercise the service's retries, missed checks, recovery thresholds, or aggregation. Keep it as a protocol probe rather than treating it as the integration runner.

Extend coverage with real sockets while retaining fake-time tests for exact boundaries. Real-network tests should verify bounded timing and event ordering, not depend on exact millisecond scheduling.

## Components

| Component | Responsibility |
| --- | --- |
| `Nina.FieldKit.Testing` | Test-only scenario model, fault-server engine, request journal, control operations, and event assertions. No dependency from the shipped plugin. |
| `Nina.FieldKit.FaultServer` | Standalone executable hosting simulated SafetyMonitor devices and a separate control endpoint. Usable while NINA is running. |
| Integration runner | Starts the server on allocated ports, instantiates the production `SafetyMonitorService`, applies a scenario, and asserts its timeline. Exposed through xUnit first. |
| NINA acceptance procedure | Points an isolated NINA test profile at the standalone server and records the actual device/setup display and plugin diagnostics. |

Use a small raw HTTP/1.1 listener for the Alpaca data endpoint. Framework servers tend to repair or reject the invalid response framing we need to emit. Use a conventional HTTP host for the control API, where valid request parsing matters. One scenario engine backs in-process and standalone modes; run at least one CI smoke test with the standalone process to catch packaging/startup differences.

The data listener supports a configurable base-path prefix, multiple device numbers, persistent connections, and the members used by the client: `interfaceversion`, `connected`, `issafe`, and interface-3 `connect`/`connecting`. Implement interface 1, 2, and 3 connection behavior, including legacy PUT `connected`. It records every PUT. The simulator has no path to observatory hardware.

Each accepted TCP connection gets a stable connection ID. Parse enough bounded HTTP request framing to handle the client's GET query parameters and PUT form bodies correctly. Structured replies echo `ClientTransactionID` and issue a monotonic `ServerTransactionID`. Raw replies bypass serialization so tests can deliberately violate the contract.

## Scenario semantics

Each scenario specifies a version, device configuration, baseline response, fault steps, a finite run limit, monitor policy, and expected observations. Validate the entire definition before starting; reject unknown fault types, ambiguous rules, invalid timing, and missing completion behavior.

- Match by **device + method + member**, with independent request ordinals for each match. Preparation requests must not consume the `issafe` fault sequence.
- Allocate a step atomically when its matching request arrives. A disconnect or timeout still consumes that step; it must not replay indefinitely because a response could not be delivered.
- A step repeats an explicit number of matching requests. On exhaustion, use the explicit `after` response. Never silently fall back to safe.
- An armed scenario initially serves its baseline. The automated runner waits for a specified production snapshot condition, such as `FreshSafe` with confirmed recovery, before starting the fault phase. The manual control command starts that same phase.
- Record the exact activation point and scenario revision. Activation affects future requests; requests already accepted retain their original response plan. Closing existing connections is a separate, explicit fault operation.
- Allow time-based phases for outages, but anchor their elapsed time to activation. Request-count scripts are preferred for exact retry-budget tests.
- Use synthetic payload fixtures only. A fixed seed reproduces server latency variation; production retry jitter is checked against its allowed range, not assumed to share that seed.

Example scenario definition (proposed contract):

```json
{
  "schemaVersion": 1,
  "id": "two-errors-then-recovery",
  "device": { "number": 0, "interfaceVersion": 3, "connected": true },
  "activation": { "mode": "armed", "baseline": { "kind": "alpaca", "value": true } },
  "match": { "method": "GET", "member": "issafe" },
  "steps": [
    { "repeat": 2, "reply": { "kind": "http", "status": 503, "body": "temporarily unavailable" } },
    { "repeat": 1, "reply": { "kind": "alpaca", "value": true } }
  ],
  "after": { "kind": "alpaca", "value": true },
  "maximumRunSeconds": 30,
  "monitorPolicy": "fast-transport",
  "activateWhen": "allSourcesFreshSafe",
  "expect": {
    "failedAttempts": 2,
    "missedChecksDuringFault": 0,
    "recoveredOnAttempt": 3,
    "aggregateNeverUnsafeDuringFault": true
  }
}
```

With the fast-transport policy below, prior safe evidence remains valid through these retries. Counters in assertions are deltas from activation, not totals that include warm-up.

## Fault primitives

Reply construction and transport timing are separate fields, so a valid safe envelope can also arrive late or over a truncated transfer.

| Primitive | Parameters and use |
| --- | --- |
| Alpaca reply | Boolean/value type, omitted fields, numeric error, message, transaction overrides; valid envelope is the default. |
| HTTP reply | Status, headers, body fixture; include HTML error pages and redirect targets pointing to a second local listener. |
| Raw reply | Exact header/body bytes or fixture file. Supports duplicate JSON keys, invalid UTF-8, invalid JSON, and broken HTTP framing. |
| Delay headers | Wait after receiving the request before sending the status line. |
| Delay/drip body | Send headers immediately, then delay body start or emit bounded chunks at intervals. Tests the whole response timeout. |
| Stall | Send nothing, headers only, or a partial body, then wait until cancellation/run expiry. |
| Close/reset | Close before headers, midway through a declared body, after a complete reply, or during idle keep-alive; choose orderly close or TCP reset. |
| Stop/restart listener | Reject new connections for a bounded phase, then resume on the same port. |
| Connection-dependent reply | Associate behavior with a connection ID to exercise stale pooled sockets and renewal. |

Complete, correctly framed HTTP containing invalid JSON is different from a truncated HTTP body. The former is a malformed success; the latter is a transport failure. Keep fixtures and expected classifications separate.

## Required scenario matrix

Run communication failures both at startup and after confirmed safety. In the latter case, record the remaining safe-evidence age so a stale deadline cannot accidentally mask the behavior under test.

| ID | Scenario | Required result |
| --- | --- | --- |
| BASE-01 | Healthy safe, then valid unsafe | Startup requires recovery confirmation; unsafe withdraws on the configured unsafe-reading threshold and ends the check without retry. |
| HTTP-01 | 408, 429, 500, 502, 503, 504, each with HTML body | Temporary failure; retry within budget. HTTP classification takes precedence over JSON parsing. |
| HTTP-02 | 301/302/307, 400, 401, 403, 404, 405, 501 | Permanent failure under the current policy; no within-check retry. A redirect's local target receives no request. Later probes use the permanent-error interval. |
| RETRY-01 | Two temporary failures, then safe | Two failed attempts, zero missed checks, recovery event on attempt 3. Recovery confirmation progress resets on each failure. |
| RETRY-02 | Three exhausted checks | Each group of three failed attempts increments missed checks once. Two misses tolerated; third withdraws safety, provided the independent age limit has not already done so. |
| RETRY-03 | 429/503 with Retry-After | Cover seconds, HTTP date, past date, invalid value, and a delay above the backoff cap. Do not retry before a valid minimum; expiry and disconnect remain responsive. |
| RETRY-04 | Several exhausted cycles, then valid safe or unsafe | Backoff grows across cycles, stays within its jitter/cap bounds, and resets on a valid observation. Valid unsafe is not a retryable error. |
| ALPACA-01 | HTTP 200 with nonzero error | `0x407` is retryable and invalidates preparation; other errors withdraw safety immediately. Never accept `Value=true` from an error envelope. |
| JSON-01 | Complete body: invalid JSON/UTF-8, array root, missing/null/string/numeric Value | Permanent failure for malformed success; never authorizes safety. A valid Boolean false remains a normal observation. |
| JSON-02 | Duplicate keys including case variants, missing/mismatched/invalid transaction IDs, wrong error-field types | Reject ambiguous or invalid envelopes. |
| JSON-03 | Omitted ErrorNumber/ErrorMessage versus explicit null or nonempty message | Omitted fields use success defaults with otherwise valid data; explicit invalid values or nonempty error text reject the observation. |
| LIMIT-01 | Bodies just below/at/above 64 KiB; declared length and streamed/chunked variants; depth 16/17 | Exercise production bounds with otherwise valid fixtures. Oversized or over-depth success never authorizes safety. |
| LIMIT-02 | Oversized headers and invalid HTTP framing | Reject safely. Assert the actual transport classification explicitly; do not mislabel these as JSON parser failures. |
| TIME-01 | Valid reply comfortably below timeout; delayed headers above timeout | Normal observation versus temporary timeout, followed by bounded retry. |
| TIME-02 | Headers immediate; body stalled or dripped past timeout | Timeout covers body consumption, not just header receipt. Late safe bytes cannot refresh evidence. |
| TIME-03 | Outage or long Retry-After crosses safe age | Getter withdraws safety at the age limit even while the worker is waiting. UI notification follows within its measured scheduling allowance. |
| TCP-01 | Close/reset before headers or midway through declared body | Temporary transport error where classified as connection/response-ended failure; partial safe data cannot authorize safety. Successful later requests recover normally. |
| TCP-02 | Listener outage and restart; server closes an idle pooled connection | No worker crash or permanent hang; new connections eventually resume valid checks. Account for transparent HTTP-handler reconnects separately from service attempts. |
| POOL-01 | Reuse followed by age-based renewal | Multiple requests use one connection before its lifetime; requests started after expiry use a new connection. No Alpaca disconnect is sent. |
| CONNECT-01 | Interface 1/2/3, initially connected/disconnected, slow or failed connect | Read-status-only sends no PUT. Managed mode uses the appropriate members within the normal attempt budget. Preparation failures are included in the attempt outcome. |
| MULTI-01 | One healthy source, one stalled source, then healthy source becomes unsafe | Required-source aggregation withdraws promptly; a stalled worker does not monopolize every available slot. Disabled sources receive no requests. |
| MULTI-02 | Sixteen sources with delays/failures | Production semaphore admits at most four simultaneous client poll operations. Verify bounded progress and collect queue delay; do not assume all sixteen have independent immediate network access. |
| LIVE-01 | Save during delayed safe response, then repeated saves | Prior result retains its original expiry. Retired-generation results cannot refresh safety; replacement workers wait for retirement. |
| STOP-01 | Disconnect during request, retry wait, and listener outage | Immediate local unsafe/disconnected result; workers terminate within deadline; no upstream disconnect command; no later observation restores safety. |
| LOG-01 | Retry/recovery, exhausted checks, malformed body containing synthetic secret marker | Normal retry/miss/recovery events remain visible without trace. Body/message markers do not leak into NINA logs or exports. Trace adds request outcomes and expires after five minutes. |

Initial implementation priority: BASE-01, HTTP-01/02, RETRY-01/02/03, JSON-01/02/03, TIME-01/02/03, TCP-01, POOL-01, and STOP-01. Follow with multi-source, live-edit, limits, and connection-handshake variants. Existing unit tests remain required throughout.

TLS is an additional lane: an untrusted or hostname-mismatched local certificate must fail closed. A trusted-success TLS case requires an explicitly provisioned test certificate in an isolated test environment; never bypass production certificate validation or silently modify the user's trust store. DNS behavior is a separate injected-handler or controlled-resolver lane because an HTTP server cannot choose the client's DNS errors.

## Timing and independent assertions

Use three policies rather than accelerating every test identically:

| Policy | Settings | Purpose |
| --- | --- | --- |
| Fast transport | poll 0.5 s; timeout 0.25 s; attempts 3; backoff 0.1 s × 2, cap 0.4 s; safe age 10 s; unsafe count 1; safe count 1; hold 0; missed checks 2 | Short real-socket tests. Generous evidence age isolates retry-count behavior. |
| Production defaults | poll 30 s; timeout 1 s; attempts 3; backoff 0.5 s × 2, cap 30 s; safe age 90 s; unsafe count 1; safe count 3; hold 10 s; missed checks 2 | Wall-clock acceptance of the actual shipped defaults, including their interaction. |
| Renewal soak | production settings with poll 5 s and connection lifetime 1,800 s; run at least 35 minutes | Frequent activity avoids idle eviction masking the 30-minute reuse limit. A separate ordinary-30-second-poll soak records natural idle reconnects. |

For count-only default-cadence tests, explicitly extend safe age to 300 s; do not claim the 90-second limit always allows all three failed checks to finish. The dedicated production-default test should expect age expiry to win when retries and post-check delays push the third check past the deadline. Keep a separate exact fake-time boundary test.

The server journal is the authority on HTTP exchanges, not on logical checks. One client `PollAsync` attempt can make multiple preparation requests; a timeout applies per HTTP exchange. The runner records completed client attempts and production snapshots/events to observe checks. A wrapper around the real client factory may assign run/check/attempt correlation IDs and timings, but must not alter outcomes, delays, or cancellation behavior.

Expected values come from the scenario and policy, not from calling the production state machine a second time. Assert both forbidden transitions and eventual required transitions. For example, a recovery scenario must prove there was no unsafe interval when valid retained evidence should cover the fault, and that confirmation counters restart correctly.

Use monotonic elapsed time for deadlines and UTC only for readable logs/HTTP-date cases. Observe production transition callbacks plus periodic snapshots. Set each test's deadline explicitly. Use broad separation between below-timeout and above-timeout delays. Timing reports include a declared scheduling allowance, initially 500 ms on shared Windows runners; exact expiry is verified by fake time, while real-time assertions bound observed latency. Do not retry failed tests automatically to turn an intermittent failure green.

Count active **client** operations separately from server handlers. A server can still be sleeping after the client has canceled and closed its socket; that does not prove the client sent overlapping operations. Conversely, a client operation that ignores cancellation must block replacement requests; retain the existing injected-handler test for that condition. HTTP handlers can also reconnect transparently before an operation returns, so TCP connection count is not the retry count.

## Standalone use with NINA

Proposed command after implementation:

```powershell
dotnet run --project tools/Nina.FieldKit.FaultServer -- --scenario scenarios/temporary-errors.json --alpaca-port 11111 --control-port 11112 --armed --output artifacts/nina-fault-run
```

It prints the exact base URL, device number, scenario/run ID, and control URL. Startup fails clearly if a requested port is occupied. Automated tests use OS-assigned ports and a readiness event, never a fixed sleep.

1. Use a separate NINA test profile with a single source pointing at the printed loopback URL. Apply the production-default policy and read-status-only mode.
2. Connect and wait for confirmed safe. This warm-up normally takes roughly two 30-second intervals with the default safe-reading count; use the actual state as the activation gate.
3. Activate the fault sequence through the control CLI/API. The controller must not call Alpaca safety routes to inspect progress, because that would consume fault steps.
4. Watch the main Setup status: retry countdown and attempt number, then recovery or a missed check. Confirm the reported NINA safe/unsafe Boolean against the scenario's expected timeline.
5. Export plugin diagnostics and NINA log evidence. The server writes a separate request/connection journal. Capture screenshots at retry, exhausted-check, expired, and recovered checkpoints.
6. Run a second-source isolation case and a live-save/late-response case. Disconnect and verify the test server sees no further driver requests after shutdown completes.

These observations validate the installed plugin and UI. Sequencer reactions are a separate manual acceptance case using simulators; the fault server must not trigger real roof/mount actions or change an existing observing profile.

The initial control interface supports status, activate, reset-to-armed-baseline, and stop. Use explicit POST mutations and GET-only status/events. A later small browser page can offer buttons for the same operations, with current phase, next scripted reply, connection count, and event timeline. Do not build a general scripting console.

Default bind is loopback only, including the control API. Reject unexpected Host/Origin values on control requests and require a per-run control token for mutations so a browser page cannot silently activate local faults. Optional LAN data-listener binding must be explicit; keep the control API local unless separately configured. Bound clients, buffers, stalls, and run lifetime. Shutdown cancels scheduled writes, joins the accept loop, closes all accepted clients, and awaits handlers before disposing shared resources.

## Evidence and CI

Each run writes:

- `scenario.json`: resolved definition, fault fixtures or hashes, server seed, and monitor policy.
- `server.jsonl`: monotonic/UTC time, run/revision/device/connection/request IDs, method/member, matched step, intended status/body size, bytes actually sent, and close/cancel reason.
- `monitor.jsonl`: attempt starts/completions, snapshots, production diagnostic events, policy revision/generation, and observed transitions.
- `result.json`: pass/fail, expected versus actual assertions, test deadlines, elapsed times, build/runtime/OS versions, and first divergence.
- xUnit/TRX results; for NINA acceptance, the exported report, selected NINA logs, and screenshots.

The run header maps each process's monotonic clock to UTC. Correlation should primarily use IDs and server-assigned request transactions, not exact cross-process clock equality. Only synthetic fault fixtures can appear in raw server evidence; production log-redaction assertions remain separate.

CI lanes:

1. **Every PR:** existing suite plus fast socket scenarios and one standalone-process smoke case; target under two minutes of test execution, ten-minute hard lane timeout. Upload evidence on failure.
2. **Release/manual:** production-default scenarios, TLS checks in an isolated environment, and a multi-source run; target 10–15 minutes with a 20-minute deadline.
3. **Opt-in soak:** 35+ minute renewal test, outage/recovery repetitions, and resource observations. Capture peak/current handles, connections, worker completion, and memory trends. Do not infer a leak solely from one garbage-collection sample.
4. **Installed NINA acceptance:** operator-driven initially, recording plugin/NINA versions and the exact scenario. UI automation can be added after the server/runner contract is stable.

Do not make incomplete new lanes a release gate until their scenarios and evidence checks are implemented and consistently passing. Once enabled, a required failed lane blocks publication; no unsigned or reduced-test fallback.

## Implementation sequence and completion criteria

1. Extract the loopback fixture into the test-only library; preserve existing tests and the shutdown-race fix. Implement request IDs, readiness, finite cancellation, and structured/raw responses.
2. Add delays, body stalls, close/reset, scripted matching, and the independent server journal. Verify each fault primitive with a simple test client before using it to judge the driver.
3. Add xUnit scenarios around the production service, activation barriers, counter deltas, and independent timeline assertions. Keep scope to the initial matrix before extending it.
4. Package the standalone server and control CLI, document one runnable NINA walkthrough, and exercise its process startup/shutdown in CI.
5. Add production-default and renewal-soak lanes, then broader limits/multi-source/live-edit cases and the optional control page.

Done means each required scenario is repeatable from its saved definition, produces both server and monitor evidence, and fails when a deliberate negative control violates its expectation. Include negative controls for accepting malformed safe data, counting every retry as a missed check, and refreshing safe age from a failed request. A fault-server implementation failure must fail the run, not be mistaken for the intended network fault. The installed-NINA checklist must explicitly record pass/fail rather than inheriting the headless suite's result.
