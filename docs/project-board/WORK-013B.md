# WORK-013B – Identity Mediation & Trust Translation

## Goal
Handle identity/trust translation when exporting between domains.

## Scope
- Translate or relay trust bundles per export policy.
- Optional namespace/service rewriting for IDs.

## Acceptance Criteria
- Target domain receives usable trust material; mTLS succeeds in bridge tests.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
