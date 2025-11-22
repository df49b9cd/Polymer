# WORK-007A – CA Bootstrap & CSR Issuance

## Goal
Deliver MeshKit CA service endpoints for CSR submission and certificate issuance.

## Scope
- CA key management (backed by KMS/HSM if available).
- CSR API, validation rules, issuance pipeline.
- mTLS enforcement on CA endpoints.

## Acceptance Criteria
- CSR -> issued cert flow works in integration tests.
- Keys stored securely; audit entry recorded for issuance.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
