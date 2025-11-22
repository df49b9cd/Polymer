# WORK-022B – Extension Lifecycle Samples (DSL/Wasm/Native)

## Goal
Create end-to-end samples for packaging, signing, deploying, and rolling out DSL, Wasm, and native extensions.

## Scope
- Sample extension artifacts; registry publish; rollout via MeshKit; validation in OmniRelay.
- Scripts to run samples and verify behavior.

## Acceptance Criteria
- Samples execute successfully in CI/staging; show telemetry and failure policies.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
