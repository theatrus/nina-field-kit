# Running the Alpaca fault tests

The fault server and runner are development tools, built separately from the NINA plugin. They use simulated devices on loopback and never discover or modify an observing profile.

## Automated tests

From the repository root with the .NET 8 SDK on Windows:

```powershell
dotnet restore Nina.FieldKit.sln --locked-mode
dotnet test Nina.FieldKit.sln -c Release --no-restore
```

This runs the existing tests plus `FaultServerTests` and `HttpFailureAuditTests`. The test project builds the standalone server for its process/control smoke test. Network cases write resolved scenarios and server/monitor JSONL under `artifacts/fault-tests/`. xUnit/TRX is the pass/fail authority for these cases. In-memory journals retain the newest 10,000 events; disk journals retain the run's full timeline.

For just the new tests:

```powershell
dotnet test tests/Nina.FieldKit.Tests -c Release --filter 'FullyQualifiedName~FaultServerTests|FullyQualifiedName~HttpFailureAuditTests'
```

The server allocates ports dynamically in automated tests. The normal suite uses real sockets and short policies; fake-time tests remain responsible for exact scheduling boundaries. A failed assertion fails CI without automatic test retries.

## Test an installed NINA plugin

Start an armed server with a chosen scenario:

```powershell
dotnet run --project tools/Nina.FieldKit.FaultServer -c Release -- --scenario tools/Nina.FieldKit.FaultServer/scenarios/temporary-errors.json --alpaca-port 11111 --control-port 11112 --output artifacts/manual-fault
```

The first output line and `artifacts/manual-fault/ready.json` contain the data URL, device number, and control endpoint. The example uses `http://127.0.0.1:11111/`, device `0`. If either port is occupied, choose other ports or pass `0` to allocate them automatically.

Use a separate NINA test profile. Select **Field Kit Alpaca Safety Monitor**, add the printed data URL/device, and choose **No — read status only**. Keep the normal 30-second policy initially. Save, connect, and wait for confirmed safe; with default confirmation counts this takes about two polling intervals.

Activate the fault only after that warm-up:

```powershell
./tools/Control-FaultServer.ps1 -ReadyFile artifacts/manual-fault/ready.json -Action activate
```

The temporary-errors scenario returns two HTTP 503 responses, then a valid safe response. Setup should show retry activity on the main status line; the check recovers on attempt 3 without incrementing missed checks. Recovery confirmation counters restart after errors even when the prior safe result remains within its age limit.

Other control actions:

```powershell
./tools/Control-FaultServer.ps1 -ReadyFile artifacts/manual-fault/ready.json -Action status
./tools/Control-FaultServer.ps1 -ReadyFile artifacts/manual-fault/ready.json -Action reset
./tools/Control-FaultServer.ps1 -ReadyFile artifacts/manual-fault/ready.json -Action pause
./tools/Control-FaultServer.ps1 -ReadyFile artifacts/manual-fault/ready.json -Action resume
./tools/Control-FaultServer.ps1 -ReadyFile artifacts/manual-fault/ready.json -Action stop
```

`reset` restores the armed baseline and clears fault ordinals; `activate` restarts the fault sequence. Requests already accepted retain their original reply plan. `pause` closes the listener and current connections; `resume` listens on the same port. Status/control requests never consume simulated safety checks.

Both endpoints bind only to loopback. Control mutations require the per-run token in the readiness file. The control host rejects foreign Origin headers and unexpected hostnames. There is no browser dashboard or remote/LAN mode in this implementation.

### Included scenarios

| File in `tools/Nina.FieldKit.FaultServer/scenarios/` | Fault after activation |
| --- | --- |
| `temporary-errors.json` | Two 503s, then normal safe responses |
| `outage.json` | Persistent HTTP 502 |
| `malformed-json.json` | Complete HTTP response containing invalid JSON |
| `late-safe.json` | Safe body delayed five seconds |
| `stalled-body.json` | Headers sent, body never completes |
| `retry-after.json` | HTTP 429 with a 120-second Retry-After |
| `unsafe.json` | Valid unsafe reading |
| `dropped-connection.json` | TCP reset |
| `invalid-chunks.json` | Invalid chunk framing |

Each example serves healthy baseline responses until activation and has a 30-minute process lifetime. The loader accepts a maximum of two hours. Use a new output directory per run to preserve evidence.

Export NINA's diagnostics at the interesting transitions, together with the selected NINA log and screenshots. Compare them with `server.jsonl`. The request journal includes User-Agent, connection ID, request transaction, scenario revision, and per-rule ordinal. A connection ID or server request is not a logical retry: preparation can send several requests per attempt, and the HTTP handler can transparently reconnect.

## Scenario format

See the committed JSON examples for the implemented schema. It is intentionally smaller than the original proposal: device definitions plus rules, baseline, finite repeated steps, and an explicit final response. Test expectations and activation barriers live in xUnit/the acceptance runner, not inside scenario JSON. Unknown properties are rejected. There is no arbitrary code execution, probabilistic fault generator, or scenario include mechanism.

Rules match device number, GET/PUT method, and member (`issafe`, `connected`, `interfaceversion`, `connecting`, `connect`). Each rule has its own ordinal; preparation calls do not consume `issafe` steps. Structured replies echo transaction IDs. Raw body/header fixtures support `${clientTransaction}` and `${serverTransaction}` substitutions.

Replies support `alpaca`, `http`, `raw`, `close`, `reset`, and `stall`; header/body delays; body chunks and chunk delays; truncation; chunked HTTP encoding; custom headers; close-after-response; omitted error fields; and Alpaca error numbers/messages. `bodyBase64` permits exact non-UTF-8 bytes. Reply and request sizes, active clients, in-memory history, and execution duration are bounded.

## Real-time acceptance

Build once, then run either suite:

```powershell
dotnet build tools/Nina.FieldKit.FaultServer -c Release
dotnet tools/Nina.FieldKit.FaultServer/bin/Release/net8.0/Nina.FieldKit.FaultServer.dll --suite defaults --output artifacts/default-policy
dotnet tools/Nina.FieldKit.FaultServer/bin/Release/net8.0/Nina.FieldKit.FaultServer.dll --suite soak --output artifacts/renewal-soak
```

`defaults` uses the shipped 30-second checks, three attempts, two tolerated missed checks, 90-second evidence age, and normal recovery confirmation. It establishes safety, activates an outage, verifies age-based withdrawal, restores the server, and verifies recovery. A local run took about 220 seconds. `result.json` records the actual outcome and elapsed time; the two JSONL files preserve the server and monitor timelines.

`soak` keeps 30-minute connection reuse and checks every five seconds for at least 35 minutes. Frequent activity avoids confusing idle eviction with lifetime expiry. It requires continuous safety, more than one observed connection, and no connection whose observed request span exceeds the lifetime allowance. It is a renewal acceptance test, not a memory-leak certification. Existing short tests check before/after renewal with a one-second policy.

Exit codes: `0` success/clean server stop; `1` invalid arguments or startup failure; `2` failed acceptance or recorded server errors. Real-time suite failures retain `result.json` and logs. Ctrl+C can abort a manual server; use `stop` for a controlled shutdown.

## CI

- **CI** runs all fast fault and HTTP-audit tests on pushes and PRs and uploads their evidence with test results.
- **Signed release** runs the fast suite and the production-default acceptance before packaging.
- **Fault acceptance** is a manual GitHub Actions workflow with `defaults` and `soak` choices and a 50-minute job limit. It uploads `result.json` and timelines even on failure.

```powershell
gh workflow run fault-acceptance.yml -f suite=defaults
gh workflow run fault-acceptance.yml -f suite=soak
```

The published plugin ZIP contains no fault-server/testing assemblies. CI never points these tools at a real observatory. Installed-NINA visual acceptance remains a separate operator-driven step; headless success does not claim that the current desktop NINA session was exercised.
