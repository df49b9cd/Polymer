# WORK-024J – Per-Item Cancellation & Priority Merge (Data Plane)

## Goal
Adopt Hugo per-item cancellation helpers and priority merge (`MergeWithStrategyAsync`) to keep data-plane streams responsive and allow prioritized sources.

## Scope
- Streaming consumers in `src/OmniRelay.DataPlane/Transport/Http/*` and `src/OmniRelay.DataPlane/Transport/Grpc/*` (duplex/client/server stream calls) and `src/OmniRelay.Dispatcher` iterating `IAsyncEnumerable` of frames/results.
- Priority source merging (e.g., control vs data, telemetry vs payload) where applicable in transport/dispatcher.

## Acceptance Criteria
- Stream loops use Hugo per-item tap/foreach helpers with linked cancellation tokens; no raw `await foreach` without cancellation linkage in these paths.
- Priority merges use `MergeWithStrategyAsync` (e.g., prefer control/telemetry over bulk) where required.
- Tests verify prompt stop on cancellation and correct priority ordering.

## Status
Planned

## SLOs & CI gates
- No measurable overhead increase per frame/item; validate with counters/benchmarks if available.
- CI: `dotnet test tests/OmniRelay.Dispatcher.UnitTests` and transport streaming tests.

## Testing Strategy
- Unit: cancellation mid-stream; priority merge ordering tests.
- Integration: duplex/client stream tests to ensure graceful shutdown and priority handling.
