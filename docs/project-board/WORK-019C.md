# WORK-019C – Runtime/Container Hardening & Vulnerability Scanning

## Goal
Harden runtime defaults (non-root, readonly FS, minimal perms) and integrate vulnerability scanning.

## Scope
- Hardened container specs; seccomp/AppArmor where applicable.
- Dependency scanning and CVE policy in CI.

## Acceptance Criteria
- Containers run non-root/readonly; security scans clean or waivers documented.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
