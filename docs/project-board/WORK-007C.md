# WORK-007C – SPIFFE/SPIRE Compatibility (Optional)

## Goal
Provide optional SPIFFE/SPIRE interoperability for identities.

## Scope
- SPIFFE ID encoding rules; CSR fields.
- Integration tests with SPIRE server (flag-gated).

## Acceptance Criteria
- When enabled, issued certs contain valid SPIFFE IDs; interoperability tests pass.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
