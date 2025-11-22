# WORK-019 – Security, Audit, and Supply-Chain Hardening

## Goal
Harden OmniRelay and MeshKit against supply-chain and operational risks with signed artifacts/configs, audit trails, and least-privilege runtime defaults.

## Scope
- Signing for binaries, containers, configs, and extension artifacts; verification in agents/OmniRelay.
- Audit logging for control-plane mutations, rollouts, certificate actions, and registry operations.
- Runtime hardening: non-root containers, readonly FS, seccomp/AppArmor profiles where applicable.
- Key management: rotation policies, HSM/KMS integration.

## Requirements
1. **End-to-end verification** – No artifact/config applied without signature/root-of-trust validation.
2. **Auditability** – Tamper-evident logs with user, time, action, context; retention policy defined.
3. **Least privilege** – Default deployments run with minimal FS/network permissions.
4. **Compliance** – SBOMs published; vulnerability scans in CI; CVE policy defined.

## Deliverables
- Signing/verification pipeline and agent/OmniRelay enforcement hooks.
- Audit log schema and sinks; CLI to query/audit.
- Hardened container specs and docs.

## Acceptance Criteria
- Unsigned/invalid artifacts rejected; event logged.
- Audit logs produced for registry, rollout, identity, and control mutations.
- Containers run non-root/readonly; security scans clean at release time.

## Testing Strategy
- Integration: signature verification failures; key rotation; audit log coverage.
- Security: container hardening checks; dependency vulnerability scans.

## References
- `docs/architecture/OmniRelay.BRD.md`
- `docs/architecture/MeshKit.BRD.md`

## Status
Needs re-scope (post-BRD alignment).
