# Codegen + Tee Rollout Harness

End-to-end sample showing how OmniRelay’s Protobuf generator and tee/shadow outbounds help API teams migrate new deployments. The harness:

- Builds `risk.proto` into a descriptor set at build time.
- Uses `OmniRelay.Codegen.Protobuf.Generator` to emit a typed service interface + client (`RiskServiceOmniRelay`).
- Hosts two in-process risk deployments (primary + shadow) implementing the generated interface.
- Adds a tee outbound via `AddTeeUnaryOutbound` so every typed client call routes to the primary deployment while mirroring to the shadow deployment.

## Run it

```bash
dotnet run --project samples/CodegenTee.Rollout
```

You should see console output similar to:

```
[risk-primary] scored ESG-42 -> 0.742
[risk-shadow] scored ESG-42 -> 0.805
Primary version (risk-primary) risk score: 0.742, limit usage: 50.0%

Shadow feed (latest):
 - risk-shadow risk score 0.805 limit usage 55.6%
Run `dotnet build` to regenerate code from Protobuf via the OmniRelay generator.
```

## Key files

| File | Purpose |
| --- | --- |
| `Protos/risk.proto` | Declares the `risk.RiskService` contract consumed by the generator. |
| `CodegenTee.Rollout.csproj` | Configures `Grpc.Tools` to emit a descriptor set and feeds it to the OmniRelay generator via `AdditionalFiles`. |
| `Program.cs` | Boots two risk deployments, wires tee outbounds, and exercises the generated `RiskServiceClient`. |
| `Program.cs` (`RiskServiceV1/V2`) | Concrete implementations of the generated interface highlighting primary vs. shadow behavior. |

## Regenerating code

Every `dotnet build` regenerates the OmniRelay bindings from the descriptor set. Edit `risk.proto`, rebuild, and the generator re-emits the `RiskServiceOmniRelay` class automatically—no manual source files to maintain.
