# WORK-014A – Capability Model & Advertisement

## Goal
Define capability schema and implement advertisement from OmniRelay/agents.

## Scope
- Capability fields (runtimes, limits, features, build epoch).
- Client-side advertisement on connect; admin visibility.

## Acceptance Criteria
- Capabilities sent on connect and visible via admin endpoints.
- Unit tests for serialization/visibility.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
