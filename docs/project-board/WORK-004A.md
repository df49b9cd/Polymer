# WORK-004A – In-Proc Host Package

## Goal
Ship OmniRelay in-proc host as a NuGet package with embedded capability manifest.

## Scope
- Host builder API for services; sample wiring.
- Generate capability manifest at publish.
- AOT publish for linux-x64/arm64; macOS dev build for validation.

## Acceptance Criteria
- Package installs into sample service; starts with provided config.
- Capability manifest emitted and readable by MeshKit.

## Status
Done — In-proc host ships as NuGet package (DataPlane/Transport/Codecs/Protos) with example capability manifest (`docs/capabilities/manifest-example.json`) and AOT-friendly defaults. Manifest doc added (`docs/architecture/capability-manifest.md`).

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
