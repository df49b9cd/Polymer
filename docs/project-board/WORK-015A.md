# WORK-015A – Policy Compute Core & Deterministic Hashing

## Goal
Build the core policy engine that produces deterministic bundles (routes, clusters, authz) with hash/epoch metadata.

## Scope
- Deterministic computation; include hash/version in outputs.
- Basic validation for references.

## Acceptance Criteria
- Same inputs → same hash; unit tests cover determinism.
- Bundles include hash/epoch metadata.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
