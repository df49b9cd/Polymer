# WORK-024G – Data-Plane Batching/Windowing (ResultPipelineChannels.WindowAsync)

## Goal
Adopt Hugo windowing for hot data-plane streams so batching is deterministic, cancelable, and compensation-aware.

## Scope
- HTTP ingress/egress buffering in `src/OmniRelay.DataPlane/Transport/Http/HttpInbound.cs` and related helpers.
- gRPC streaming calls in `src/OmniRelay.DataPlane/Transport/Grpc/*` (client/server/duplex stream call classes).
- Dispatcher fan-in/out buffering in `src/OmniRelay.Dispatcher` where batches are formed.
- Tee/outbound buffering in `src/OmniRelay.Codecs` (if batching exists).

## Acceptance Criteria
- Batching uses `ResultPipelineChannels.WindowAsync` (size + interval thresholds) with bounded channels; no raw ad-hoc timers for flush.
- Consumers process windows with `Result.MapStreamAsync`/`ForEachAsync`, propagating failures via `Result` and compensations.
- Thresholds configurable; cancellation flushes remaining items deterministically. Hot-path allocations stay flat (validate with counters).

## Status
Planned

## SLOs & CI gates
- No regression in transport p99 for unary/streaming; document any change. Monitor allocation rate via `dotnet-counters` before/after.
- CI: `dotnet test tests/OmniRelay.Dispatcher.UnitTests` and transport feature/integration slices; `./eng/run-ci.sh` for parity.

## Testing Strategy
- Unit: windowing tests for transport/dispatcher buffers (size/interval flush, cancellation flush).
- Integration: dispatcher/transport feature tests that stream payloads; ensure no data loss and bounded buffering.
- Hyperscale: run if batching impacts throughput benchmarks.
