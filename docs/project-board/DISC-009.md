# DISC-009 – Security Bootstrap Integration

## Goal
Integrate OmniRelay with a workload identity provider (SPIFFE/SPIRE or cloud-native solutions) so every mesh node obtains mTLS credentials and policy-enforced roles during bootstrap.

## Scope
- Implement identity issuance workflow: node attests (workload identity, image signature), receives short-lived cert embedding role + cluster metadata.
- Wire issued certs into gossip + control-plane transports (TLS 1.3, approved cipher suites).
- Support bootstrap tokens / policy CRDs defining which identities may join which clusters/roles.
- Provide revocation + rotation flows with zero/low downtime.

## Requirements
1. **PKI integration** – Support SPIFFE/SPIRE to start, but design for pluggable providers (Azure Managed Identity, AWS IAM).
2. **Certificate lifecycle** – Auto-renew before expiry, maintain overlapping validity windows, and emit alerts if renewal fails.
3. **Policy enforcement** – Validate join requests against policy (role allowlists, environment tags) before issuing certs.
4. **Auditing** – Log enrollment, renewal, and revocation events with caller identity + metadata.
5. **Documentation** – Provide threat model + hardening guide covering bootstrap security posture.

## Deliverables
- Bootstrap service/library handling attestation and certificate issuance.
- Configuration schema for policies + integration instructions for each provider.
- Unit/integration tests for issuance, renewal, revocation, and failure handling.
- Updated architecture/security docs.

## Acceptance Criteria
- Nodes cannot join without valid certs; attempts are rejected and logged.
- Certificate rotation occurs without disrupting gossip/control traffic (observed via tests).
- Operators can revoke a node and see it removed from the mesh immediately.

## References
- `docs/architecture/service-discovery.md` – “Secure peer bootstrap”, “Risks & mitigations”.
