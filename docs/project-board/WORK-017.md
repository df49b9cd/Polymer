# WORK-017 – Operator UX & CLI (OmniRelay + MeshKit)

## Goal
Deliver a unified, AOT-safe CLI and operator UX for configuring, monitoring, and troubleshooting OmniRelay and MeshKit across roles and deployment modes.

## Scope
- CLI verbs for config diff/apply/simulate, rollout control, extension registry operations, capability inspection, and failover commands.
- Output formats: table/JSON with golden tests; machine-friendly exit codes.
- Auth: mTLS + tokens; RBAC scopes aligned to control APIs.
- Ergonomics: profile/config files, completions, clear error messaging.

## Requirements
1. **AOT compliance** – CLI builds/publishes AOT; no reflection-based plugins.
2. **Coverage** – Supports central/agent/bridge roles and all OmniRelay modes.
3. **Safety** – Prompts/guards for destructive actions; dry-run and simulate modes.
4. **Diagnostics** – Commands to inspect capability sets, current epoch, LKG status, extension health.

## Deliverables
- CLI implementation + test fixtures; completions; packaging.
- UX docs and examples; integration in samples.

## Acceptance Criteria
- CLI verified against live fixtures for core flows (apply, simulate, rollout, registry ops, failover).
- Golden tests stable; AOT publish green.
- RBAC errors surfaced with actionable remediation.

## Testing Strategy
- Unit: command parsing, formatting, validators.
- Integration: CLI -> control APIs under mTLS; failure/permission cases.

## References
- `docs/architecture/MeshKit.BRD.md`
- `docs/architecture/OmniRelay.BRD.md`

## Status
Needs re-scope (post-BRD alignment).
