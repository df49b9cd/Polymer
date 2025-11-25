# WORK-024A – Pipeline Timers (ResultPipelineTimers.DelayAsync)

## Goal
Replace ad-hoc `Task.Delay` loops with Hugo pipeline-aware timers so delays are deterministic, cancellation-safe, and AOT-optimized.

## Scope
- Control watch backoff in `src/OmniRelay.ControlPlane/Core/Agent/WatchHarness.cs`.
- Leadership evaluation/heartbeat pacing in `src/OmniRelay.ControlPlane/Core/LeadershipCoordinator.cs`.
- Gossip intervals (heartbeat/shuffle/suspicion) in `src/OmniRelay.ControlPlane/Core/Gossip/MeshGossipHost.cs`.

## Acceptance Criteria
- All delays in the files above use `ResultPipelineTimers.DelayAsync` (via a tiny helper or direct calls) with a `ResultPipelineStepContext` that links the active `TimeProvider` and cancellation token.
- No bare `Task.Delay` remains in these paths; cancellations surface as `Error.Canceled` in logs/telemetry.
- Unit/feature tests updated to assert deterministic timing with a fake/virtual `TimeProvider` where applicable.

## Status
Planned

## SLOs & CI gates
- No added allocations in hot loops (verify with `dotnet-counters` allocation rate before/after on control-plane smoke).
- CI: `dotnet test tests/OmniRelay.Core.UnitTests/OmniRelay.Core.UnitTests.csproj`.

## Testing Strategy
- Unit: add/adjust tests for watch backoff and leadership pacing using virtual time.
- Integration: control-plane smoke (MeshKit AOT smoke) to confirm no regressions.
- Feature/Hyperscale: not required unless timing metrics change; document rationale in PR.
