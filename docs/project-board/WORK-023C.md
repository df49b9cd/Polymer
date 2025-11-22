# WORK-023C – MeshKit Integration & Regression Tests

## Goal
Adopt the shared transport/codec/proto packages inside MeshKit and OmniRelay build/test pipeline, removing any duplicated transport/codec code in MeshKit. MeshKit should depend on `OmniRelay.ControlPlane` + shared packages, not on `OmniRelay.DataPlane` internals.

## Scope
- Replace MeshKit transport/codec usages with shared libraries; remove duplicated implementations.
- Ensure OmniRelay.DataPlane and OmniRelay.ControlPlane reference the packages (not source) in build/test.
- Contract tests ensuring MeshKit uses the same behaviors as OmniRelay (in-proc fixtures/in-memory server) and guard against future drift in CI.

## Acceptance Criteria
- MeshKit builds against shared packages and `OmniRelay.ControlPlane`; no duplicated transport/codec code remains; `OmniRelay.DataPlane` stays data-plane only.
- Regression tests pass in CI (MeshKit + OmniRelay solution).

## Status
Done — Control-plane vs data-plane split is complete; shared packages (`OmniRelay.Transport`, `OmniRelay.Codecs`, `OmniRelay.Protos`, `OmniRelay.ControlPlane.Abstractions`) are packable with SBOMs. MeshKit (via `OmniRelay.ControlPlane` + tests in `OmniRelay.MeshKit.AotSmoke`) consumes control-plane runtime and shared packages; data-plane no longer carries gossip/leadership/shard hosting code, removing duplicated transport/codec implementations. CI build succeeds.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
