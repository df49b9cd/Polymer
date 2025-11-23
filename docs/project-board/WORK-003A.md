# WORK-003A – DSL Host MVP

## Goal
Ship the AOT-friendly DSL interpreter with signed package validation, opcode allowlist, and quotas.

## Scope
- Manifest + signature checks for DSL packages.
- Opcode allowlist and resource quotas (time/memory) with policy defaults.
- Minimal telemetry for load/failure events.

## Acceptance Criteria
- Unsigned/invalid DSL packages rejected.
- Quota breach triggers configured policy and emits telemetry.
- Unit/integration tests run in all modes; AOT publish passes.

## Status
Done (DSL host delivered; Wasm/native deferred per WORK-003B/003C)

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
