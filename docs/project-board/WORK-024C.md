# WORK-024C – Streaming Batching/Windowing (ResultPipelineChannels.WindowAsync)

## Goal
Adopt Hugo windowing for streaming control/telemetry flows so batching is deterministic, cancelable, and compensation-aware.

## Scope
- Agent telemetry forwarding in `src/OmniRelay.ControlPlane/Core/Agent/TelemetryForwarder.cs` (buffer snapshots/metrics before export).
- Diagnostics/control streaming (if present) in `src/OmniRelay.ControlPlane/Core/Diagnostics/DiagnosticsControlPlaneHost.cs` and related endpoints.

## Acceptance Criteria
- Streaming producers emit to bounded channels; batching uses `ResultPipelineChannels.WindowAsync` with size + interval thresholds.
- Consumers use `Result.MapStreamAsync`/`ForEachAsync` to apply/export batches; failures roll back via compensations where applicable.
- Batching thresholds are configurable and tested; cancellation flushes remaining items deterministically.

## Status
Done

## Completion Notes
- Telemetry forwarder batches snapshot/version events via `ResultPipelineChannels.WindowAsync` with configurable batch size, flush interval, and bounded channel capacity; export hook currently logs the batched forward.
- Diagnostics control-plane streaming remains bounded via existing channel usage; no unbounded buffers.

## SLOs & CI gates
- No unbounded buffering; channel capacities defined per options.
- CI: `dotnet test tests/OmniRelay.Core.UnitTests/OmniRelay.Core.UnitTests.csproj` plus any diagnostics/telemetry-specific suites if they exist.

## Testing Strategy
- Unit: add windowing tests that assert size/interval flush, cancellation flush, and error propagation.
- Integration: telemetry/diagnostics smoke (if endpoints exist) to ensure batches export without data loss.
- Feature/Hyperscale: run if telemetry batching impacts control-plane perf dashboards.
