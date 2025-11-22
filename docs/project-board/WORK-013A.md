# WORK-013A – Bridge Subscription & Export Policy Enforcement

## Goal
Implement bridge subscription to a source domain and enforce export allowlists/denylists.

## Scope
- Subscribe to control streams; filter state per export policy.
- Configuration for export rules with audit.

## Acceptance Criteria
- Only allowed resources exported; blocked items logged/audited.
- Integration test with dual domains confirms policy enforcement.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
