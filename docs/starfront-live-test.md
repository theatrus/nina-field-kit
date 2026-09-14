# Starfront live compatibility test

**Updated result, 22:53 UTC: compatibility passes.** The original strict-envelope rejection below is retained as test history. After verifying ASCOM Library's behavior, Field Kit now defaults an omitted ErrorNumber to zero and an omitted ErrorMessage to an empty string. Present invalid fields, explicit errors (including a nonempty message with code zero), and missing/non-Boolean safety values still fail. Three production-client polls at 22:53:10–22:53:14 UTC passed and each reported `IsSafe=false`, with IsSafe request durations of approximately 62–76 ms. No upstream writes were issued. All 94 automated tests pass.

The implementation evidence is ASCOM Library commit `afff74709a1c78ed443f1e53567dd70a7d13f9c7`: [ErrorResponse defaults](https://github.com/ASCOMInitiative/ASCOMLibrary/blob/afff74709a1c78ed443f1e53567dd70a7d13f9c7/ASCOM.Common/ASCOM.Common.Alpaca/AlpacaResponses/ErrorResponse.cs) and [RemoteDevice error handling](https://github.com/ASCOMInitiative/ASCOMLibrary/blob/afff74709a1c78ed443f1e53567dd70a7d13f9c7/ASCOM.Alpaca/ASCOM.Alpaca.Clients/RemoteDevice.cs). The wire specification and the official client's tolerance differ; matching that tolerance resolves this interoperability issue without substituting a safety reading.

## Initial strict-parser test (superseded)

Tested September 13, 2026, approximately 22:37–22:40 UTC, using the public endpoint in [Starfront's Safety Monitor guide](https://docs.starfront.space/guides/ascom_alpaca/safety). Building 4 is the guide's example, not an assumed assignment for the user's equipment. No NINA profile was changed.

Base URL: `https://alpaca-api.tx.starfront.space` (HTTPS port 443). Device number: `4`. All requests were GETs; no upstream connection or device state was modified.

## Observed responses

| Property | HTTP status | Raw Value | Request duration |
| --- | --- | --- | --- |
| interfaceversion | 200 | 3 | 325 ms |
| connected | 200 | true | 222 ms |
| issafe | 200 | false | 187 ms |

All three responses contained Value, ClientTransactionID, and ServerTransactionID, but omitted ErrorNumber and ErrorMessage. For example, the IsSafe response was:

```json
{"Value":false,"ClientTransactionID":3,"ServerTransactionID":4888480}
```

This is a point-in-time raw reading, not an independently verified assessment of roof/weather conditions. The endpoint was reachable, but its envelope did not pass the plugin's protocol validation.

## Actual Field Kit client result

The production AlpacaSafetyClient was invoked from a new read-only probe tool using the default one-second timeout, 30-second safe cache lifetime, and 1800-second connection lifetime. It rejected the first response (interfaceversion) and stopped before establishing a safety observation:

```text
2026-09-13T22:39:44.4035659+00:00
Outcome: PermanentFailure
Reason: Missing required Alpaca ErrorNumber or ErrorMessage field
ServerTransactionId: 4899780
Elapsed: approximately 283 ms
IsSafe: null (not accepted as an observation)
```

The [ASCOM Alpaca API response contract](https://ascom-standards.org/api/) requires error fields even for successful transactions. We preserved strict validation rather than assuming an omitted error code means success. The client now identifies missing error fields specifically and retains the server transaction ID for troubleshooting. A reduced regression fixture covers both true and false values in this incomplete envelope.

Compatibility therefore **fails at protocol validation** with the currently observed server response. The provider will remain unsafe. Live cache recovery, sustained polling, and renewal behavior could not be validated against this endpoint because no valid observation was admitted. Those behaviors remain covered by the fake-clock and loopback tests. Installed-NINA behavior was not tested here.

## Reproduce

```powershell
dotnet run --project tools/Nina.FieldKit.Probe -c Release -- https://alpaca-api.tx.starfront.space 4 3
```

The final argument is a sample limit (1–30). The probe uses externally managed/read-only mode, never changes NINA settings, and stops immediately on permanent protocol failure. Exit code 0 means valid observations were received, even if their Boolean value was unsafe; 2 means a transport/protocol failure. It emits JSON lines with UTC timestamps and results. It is explicit opt-in tooling; the unit tests never contact Starfront.

Resolution requires the upstream envelope to supply the required success/error fields, or a separately designed and explicitly selected compatibility policy. No such policy was enabled by this test. The full automated suite passes 81 tests; existing NINA transitive-package compatibility warnings remain.
