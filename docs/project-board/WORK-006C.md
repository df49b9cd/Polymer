# WORK-006C – Capability Negotiation Handshake

## Goal
Add capability exchange between OmniRelay/agents and MeshKit, tailoring payloads to supported features.

## Scope
- Client advertises capabilities (runtimes, limits, build epoch).
- Server tailors payloads or rejects with actionable errors.
- Tests for mixed-capability fleets.

## Acceptance Criteria
- Nodes with missing capabilities receive down-leveled payloads or clear rejection; LKG used safely.
- Metrics/logs emitted for negotiation outcomes.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
