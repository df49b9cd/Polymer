# DISC-010 – Join & Certificate Tooling

## Goal
Provide operators and CI systems with CLI utilities to issue, join, rotate, and revoke mesh credentials safely.

## Scope
- Implement `omnirelay cert issue` (generate CSR/submit), `omnirelay mesh join` (retrieve cert, write config, test connectivity), and `omnirelay mesh revoke` (invalidate cert + optionally cordon node).
- Support both interactive (prompt-driven) and automated (flags/env) modes.
- Integrate with the security bootstrap service (DISC-009) for attestation and policy checks.
- Generate documentation and examples for onboarding new clusters/nodes.

## Requirements
1. **UX** – Commands must emit clear instructions, show progress, and support `--dry-run`.
2. **Security** – Sensitive materials (private keys, tokens) stored securely with correct permissions; CLI must warn if filesystem permissions are weak.
3. **Validation** – After join, tool should run a health probe (gossip + control endpoint) to confirm membership.
4. **Logging** – Operations log to stdout and audit stream including actor, target node, policy used.
5. **Compatibility** – Tools must run on macOS, Linux, and Windows environments with .NET SDK prerequisites documented.

## Deliverables
- CLI command implementations + automated tests.
- Documentation pages (QuickStart + Troubleshooting) under `docs/reference`.
- Sample scripts for CI/CD integration (e.g., GitHub Actions, Azure Pipelines).

## Acceptance Criteria
- Running `omnirelay mesh join` on a fresh host results in the node appearing in `/control/peers` with correct metadata.
- Revoking credentials immediately blocks the node and removes its access (verified via tests).
- Documentation reviewed by SREs and includes screenshots/log snippets.

## Testing Strategy

### Unit tests
- Add coverage for CLI argument parsing, interactive prompts, and non-interactive flag combinations so misconfigurations produce clear errors.
- Test filesystem helpers that create key stores, enforce POSIX/NTFS permissions, and redact sensitive output before logging.
- Validate post-join health probes to ensure gossip + control endpoints are polled and failures bubble up with remediation hints.

### Integration tests
- Exercise `omnirelay cert issue/join/revoke` against the bootstrap service using temporary nodes to confirm attestation, certificate download, and revocation flows succeed on macOS/Linux/Windows runners.
- Run `--dry-run` and automation modes inside CI scripts to confirm environment variable overrides, non-interactive secrets, and exit codes behave consistently.
- Validate the CLI emits audit records and structured logs for each operation, matching the security integration expectations.

### Feature tests

#### OmniRelay.FeatureTests
- Follow the onboarding workflow end to end: issue credentials, join the mesh, verify `/control/peers`, rotate the certificate, and revoke access while capturing CLI + documentation outputs for accuracy.
- Simulate operator missteps (filesystem permission issues, bootstrap denials) to confirm troubleshooting guidance and CLI remediation hints unblock the workflow.

#### OmniRelay.HyperscaleFeatureTests
- Automate bulk enrollments via CI to prove non-interactive flows handle concurrent joins, rotations, and revocations without leaking secrets or exhausting quotas.
- Stress negative scenarios at scale (expired tokens, wrong policies) to ensure the CLI surfaces actionable errors and audit logs stay correlated with the large batch operations.

## References
- `docs/architecture/service-discovery.md` – “Secure peer bootstrap”, “Transport & encoding strategy”.
