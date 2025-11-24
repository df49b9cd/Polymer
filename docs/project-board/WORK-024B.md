# WORK-024B – Retry/Backoff via Result.RetryWithPolicyAsync

## Goal
Standardize retries/backoff on Hugo `Result.RetryWithPolicyAsync` + `ResultExecutionPolicy` instead of hand-rolled loops, improving observability and AOT safety.

## Scope
- Control watch reconnect flow in `src/OmniRelay.ControlPlane/Core/Agent/WatchHarness.cs` (resume/connect attempts).
- Leadership lease acquire/renew in `src/OmniRelay.ControlPlane/Core/LeadershipCoordinator.cs`.
- Gossip send paths (shuffle/heartbeat RPCs) in `src/OmniRelay.ControlPlane/Core/Gossip/MeshGossipHost.cs`.

## Acceptance Criteria
- Each retryable operation is wrapped in `Result.RetryWithPolicyAsync` with a clearly defined policy (fixed/exponential) and bounded attempts; backoff hints come from policy, not manual delays.
- Errors and cancellations are returned as `Result` (no thrown exceptions in business logic). Logs/metrics include attempt counts and last error code.
- Configurable policies injected via DI; tests can override with deterministic time providers.

## Status
Planned

## SLOs & CI gates
- No increase in p99 for control watch resume or leadership renew paths (compare to pre-change baseline).
- CI: `dotnet test tests/OmniRelay.Core.UnitTests/OmniRelay.Core.UnitTests.csproj`; optional focused perf smoke if policy timings change.

## Testing Strategy
- Unit: add retry policy coverage for watch reconnect and lease renew (success after N failures, cancellation propagation).
- Integration: leadership/gossip smoke (ShardControlPlaneTestHost + MeshKit AOT smoke) to ensure retries don’t mask failures.
- Feature/Hyperscale: run if policy changes affect control-plane throughput; otherwise document rationale.
