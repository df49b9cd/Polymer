# WORK-024P – MeshKit Tutorial Alignment (Hugo Patterns)

## Goal
Apply the Hugo MeshKit tutorial patterns (peer discovery, gossip, leader election with channels/result pipelines) to our MeshKit-related code paths.

## Scope
- MeshKit agent/control wiring in `src/OmniRelay.ControlPlane/Core/Agent/*` and `src/OmniRelay.ControlPlane/Core/Gossip/MeshGossipHost.cs`.
- Leadership/gossip integration surfaces in `src/OmniRelay.ControlPlane/Core/LeadershipCoordinator.cs` and `src/OmniRelay.ControlPlane/Core/LeadershipEventHub.cs`.
- MeshKit smoke host/tests in `tests/OmniRelay.MeshKit.AotSmoke` and any MeshKit fixtures in `tests/TestSupport`.
- Documentation touchpoints in `docs/knowledge-base/meshkit/*` and `docs/reference/meshkit-control-plane-story.md`.

## Acceptance Criteria
- Gossip/leadership/agent flows use Hugo channels, `WaitGroup`, and Result pipelines per the MeshKit tutorial (no ad-hoc `Task.WhenAll/Delay`).
- Control watch + CA flows are policy-driven (ResultExecutionPolicy) and timer-driven via `ResultPipelineTimers`.
- MeshKit AOT smoke test passes with zero trimming warnings; no reflection-only paths introduced.
- Docs updated to reflect the standardized Hugo patterns for MeshKit control plane and agent behaviors.

## Status
Planned

## SLOs & CI gates
- Maintain existing MeshKit AOT smoke success (`tests/OmniRelay.MeshKit.AotSmoke`).
- CI: `dotnet test tests/OmniRelay.MeshKit.AotSmoke/OmniRelay.MeshKit.AotSmoke.csproj` and relevant control-plane/core unit tests.

## Testing Strategy
- Unit: cover gossip/leadership timers and agent watch retry/backoff with fake time providers.
- Integration: MeshKit AOT smoke host + control-plane fixtures to ensure end-to-end discovery/leadership still work under Hugo primitives.
- Feature/Hyperscale: run if changes impact cross-node coordination throughput; document rationale otherwise.
