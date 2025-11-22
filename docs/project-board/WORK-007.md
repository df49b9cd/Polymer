# WORK-007 – Epic: Identity & CA Service

Split into iteration-sized stories (A–D).

## Child Stories
- **WORK-007A** – CA bootstrap & CSR issuance
- **WORK-007B** – Rotation/renewal workflows & trust bundle delivery
- **WORK-007C** – SPIFFE/SPIRE compatibility (optional feature flag)
- **WORK-007D** – Audit/metrics/alerts for identity

## Definition of Done (epic)
- Certificates issued/rotated automatically for OmniRelay/agents; observability and audit in place.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
