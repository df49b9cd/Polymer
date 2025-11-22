# WORK-007 – Identity & CA Service (CSR, Issuance, Rotation)

## Goal
Provide MeshKit-hosted identity/CA services that issue, rotate, and revoke certificates for OmniRelay nodes (all modes) with trust-bundle distribution and SPIFFE compatibility.

## Scope
- CSR endpoints and approval pipeline; issuance and renewal schedules; revocation handling.
- Trust bundle distribution to agents/OmniRelay; pinning and rotation workflows.
- Optional SPIFFE/SPIRE interop; SAN/URI encoding rules for services and roles.
- Audit logging and metrics for issuance/rotation failures.

## Requirements
1. **Security** – mTLS on CA endpoints; signed responses; key storage backed by HSM or encrypted KMS where available.
2. **Rotation** – Automatic renewal before expiry; failure backoff; LKG cert retention policy.
3. **Compatibility** – Support central + agent roles; certs carry capability/role info where needed.
4. **Observability** – Metrics/logs/alerts for issuance latency, failure rate, and time-to-expiry.

## Deliverables
- CA service in MeshKit with CSR API, issuance logic, rotation jobs, and trust-bundle publication.
- Agent/client logic in OmniRelay/MeshKit agent to request/renew certs and cache bundles.
- Docs for ops (bootstrap, rotate, revoke) and security (key handling, audit).

## Acceptance Criteria
- OmniRelay/agents obtain and renew certs automatically; expired certs are rotated without traffic loss (via LKG fallback where applicable).
- SPIFFE-compatible IDs validated in integration tests (optional feature flag).
- Audit logs show who/what/when for issuance and revocation.

## Testing Strategy
- Integration tests for CSR -> issue -> rotate -> revoke flows.
- Chaos tests for CA unavailability and clock skew; ensure LKG cert grace period works.

## References
- `docs/architecture/MeshKit.SRS.md`
- `docs/architecture/MeshKit.BRD.md`

## Status
Needs re-scope (post-BRD alignment).
