# Bootstrap Threat Model & Hardening Guide

## Scope

This document tracks the security posture of the MeshKit bootstrap workflow (originally tracked as discovery story 009): attestation, token issuance, workload identity minting, policy enforcement, and certificate lifecycle (renewal + revocation). It complements the broader service-discovery architecture note.

## Assets

- Mesh trust anchors (SPIFFE/SPIRE signing keys, trust bundles).
- Bootstrap tokens and associated metadata (role, cluster, max uses, audit GUID).
- Policy documents/CRDs that define who may join the mesh.
- Workload certificates and private keys delivered to dispatchers.
- Audit logs for enrollment, renewal, and revocation.

## Threats & mitigations

| Threat | Impact | Mitigation |
| --- | --- | --- |
| Stolen bootstrap token | Rogue node joins mesh | Tokens embed cluster + role + lifetime + max uses; policy evaluator re-checks posture; audit IDs allow rapid revocation; tokens issued via CLI guarded secrets. |
| Compromised workload certificate | Attacker impersonates node | Revocation API/CLI propagates eviction through gossip/control planes; TransportTlsManager reloads bundles without restart; bootstrap renewal alerts surface anomalies. |
| Malicious policy change | Backdoor role/cluster access | Policies live in versioned CRDs with schema validation; reload logs include doc name/version; require code review + signed commits. |
| Identity provider compromise | Fake certificates minted | Workload identity providers are pluggable; SPIFFE/SPIRE signing keys stored via secret provider; monitor renewal anomalies + trust-bundle drift; rotate CA + reissue identities periodically. |
| Replay of attestation evidence | Old posture reused to rejoin | Attestation metadata + bootstrap token GUID logged; policy evaluator can require freshness windows; bootstrap service tracks last-issued identities per node and blocks rapid re-entry after revocation. |
| Renewal outage | Certificates expire fleet-wide | Renewal background service uses jitter/backoff and alerts when renewals fall below SLO; operators can fall back to manual CLI issuance; doc describes safe-mode window. |

## Operational runbook highlights

1. **Enroll** – Operator issues short-lived token, node runs omnirelay mesh bootstrap join with attestation, receives bundle, writes to disk, reloads dispatcher.
2. **Renew** – Renewal agent watches expiry horizon (default 20% life remaining), requests a new bundle via bootstrap service, swaps certificate atomically.
3. **Revoke** – Operator locates audit ID (token GUID or SPIFFE ID) via CLI/logs, executes revocation command, ensures gossip/control-plane metrics show eviction, optionally rotates trust bundle if root compromise suspected.
4. **Policy update** – Update CRD/JSON policy, push through git/CI, bootstrap service reloads file and logs version; monitor bootstrap.policy.reload metric for failures.

## Testing expectations

- Unit tests: token replay protection, policy parsing, SPIFFE certificate builder, renewal scheduler edge cases.
- Integration tests: dispatcher host joining via bootstrap, CLI workflows, revocation drill, audit log verification.
- Feature tests: zero-touch provisioning, CLI visibility, gossip/control-plane convergence using issued identities.
- Hyperscale feature tests: concurrent issuance/rotation across hundreds of nodes, revocation fan-out, alert noise validation.

## Future work

- Wire up additional identity providers (Azure Managed Identity, AWS IAM Roles Anywhere).
- Extend renewal agent to support on-host TPM/HSM key protection.
- Automate CA key rotation with trust bundle dissemination + staged dispatcher restarts.
