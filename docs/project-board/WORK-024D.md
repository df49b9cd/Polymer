# WORK-024D – Mixed Orchestration → Streaming (FanOut + Merge)

## Goal
Align list-based orchestration stages with streaming continuations using Hugo `ResultPipeline.FanOutAsync` and `ResultPipelineChannels.MergeAsync`, keeping compensations intact across boundaries.

## Scope
- Shard control simulations and bulk shard queries in `src/OmniRelay.ControlPlane/Core/Shards/ControlPlane/ShardControlPlaneService.cs` (fan-out compute per shard → merge diffs/results to consumers).
- Any control-plane host utilities that launch per-endpoint tasks then stream results (review `tests/TestSupport/Shards/ShardControlPlaneTestHost.cs` for fixture alignment).

## Acceptance Criteria
- Fan-out over shard sets uses `ResultPipeline.FanOutAsync` (or `Result.WhenAll` with policies) returning `Result` values; downstream streaming uses `MergeAsync` to unify outputs with cancellation/compensation wired.
- No manual `Task.WhenAll/WhenAny` remains in these flows; cancellations propagate as `Error.Canceled`.
- Tests cover partial-failure fan-out (one shard fails, others succeed) and ensure compensations/cleanups run.

## Status
Done

## Completion Notes
- Shard simulations fan-out per-shard reconciliation using `ResultPipeline.FanOutAsync` under the repository retry policy and merge worker outputs with `ResultPipelineChannels.MergeAsync`, keeping cancellation/compensation threading intact.
- Unknown assignments now return `shards.control.assignment.missing`; worker queues complete cleanly before merge.


## SLOs & CI gates
- Maintain current p99 latency for shard list/diff operations; document any change.
- CI: `dotnet test tests/OmniRelay.Core.UnitTests/OmniRelay.Core.UnitTests.csproj` and shard integration tests in `tests/OmniRelay.IntegrationTests/ShardControlPlaneIntegrationTests.cs`.

## Testing Strategy
- Unit: shard service fan-out tests with mixed success/failure results.
- Integration: shard control integration and MeshKit AOT smoke to validate merged streams.
- Feature/Hyperscale: run if shard fan-out touches hyperscale flows.
