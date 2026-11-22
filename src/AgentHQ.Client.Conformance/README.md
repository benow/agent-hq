# AgentHQ.Client.Conformance

Conformance test kit for the Agent HQ hub-and-spoke wire contract (v1). Run it against any
hub implementation to prove the contract's load-bearing rules:

- contract-header enforcement (mismatched major → 400 naming both versions)
- heartbeat response shape (`lastAcceptedSeq`, `commandsPending`, `serverVersion`, `contractVersion`)
- worker own-id scoping (another worker's id → 404)
- the three-case ordering rule (equal applies / duplicate trims / ahead rejects 422 with `expected`)
- command-result idempotence by `commandId`, with the canonical `resultHash` formula
- snapshot repository-ownership exclusivity (conflict → 409)
- the CI ingest payload shape

## Usage

```csharp
var results = await AgentHqConformance.RunAsync(new AgentHqConformance.Host(
    NewWorkerAsync: async () => { /* provision a fresh worker identity; return (client, workerId) */ },
    EnqueueCommandAsync: (workerId, kind, actor, payload) => { /* call your hub's queue service */ },
    NewCiClientAsync: async () => { /* provision a ci-role client */ },
    AdminClient: adminHttpClient));

foreach (var r in results)
    Console.WriteLine($"{r.Name}: {(r.Passed ? "PASS" : "FAIL")} — {r.Detail}");
```

See `AgentHQ.Client` for the contract DTOs and the typed client.
