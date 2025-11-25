# WORK-024H – Data-Plane Mixed Orchestration + Streaming

## Goal
Align list-based orchestration steps with streaming continuations using Hugo `ResultPipeline.FanOutAsync` and `ResultPipelineChannels.MergeAsync`, keeping compensations intact across transport/dispatcher flows.

## Scope
- Dispatcher request fan-out/merge in `src/OmniRelay.Dispatcher` (selective shard/partition routing).
- HTTP multi-endpoint flows (e.g., mirror/tee) in `src/OmniRelay.DataPlane/Transport/Http/*`.
- gRPC multi-endpoint/duplex flows in `src/OmniRelay.DataPlane/Transport/Grpc/*`.

## Acceptance Criteria
- Fan-out over targets uses `ResultPipeline.FanOutAsync` (or `Result.WhenAll` with policies); downstream streams merge via `MergeAsync`/`MergeWithStrategyAsync` with cancellation linked.
- No raw `Task.WhenAll/WhenAny` in these paths; cancellations surface as `Error.Canceled` with compensations executed.
- Tests cover partial failure (one leg fails, others succeed) and ensure compensations/cleanups run.

## Status
Done

## Completion Notes
- Dispatcher lifecycle fan-out now runs start steps through `ResultPipeline.FanOutAsync` with ordered readiness merged via channels, eliminating raw ErrGroup usage.
- Gossip/data-plane batch/merge already use pipeline-aware window/for-each; no remaining Task.WhenAll/WhenAny in targeted paths.

## SLOs & CI gates
- Maintain current dispatcher p99 for fan-out scenarios; document changes.
- CI: `dotnet test tests/OmniRelay.Dispatcher.UnitTests` and integration/feature suites that cover multi-endpoint routing.

## Testing Strategy
- Unit: fan-out/merge tests with mixed success/failure.
- Integration: dispatcher integration tests and transport feature tests for multiplexed calls.
- Hyperscale: run if changes touch hyperscale fan-out paths.
