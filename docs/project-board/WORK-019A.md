# WORK-019A – Signing/Verification Pipeline

## Goal
Implement signing for binaries, containers, configs, and extension artifacts with verification in agents/OmniRelay.

## Scope
- Integrate signing into build/publish; verification hooks on load/apply.
- Key management with rotation hooks.

## Acceptance Criteria
- Unsigned/invalid artifacts/configs rejected in tests; signatures verified in CI.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
