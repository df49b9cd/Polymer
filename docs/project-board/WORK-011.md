# WORK-011 – Rollout Manager (Canary, Fail-Open/Closed, Kill Switch)

## Goal
Implement rollout orchestration for configs and extension bundles with staged canary, automated health gates, fail-open/closed choices, and remote kill switches.

## Scope
- Rollout plans: stages, scope (namespace/region/percent), success criteria, timeouts.
- Health evaluation: tie telemetry (WORK-012) to rollout gates; regressions trigger pause/rollback.
- Kill switch per extension/policy; remote disable propagates via control protocol.
- Persistent history and audit of rollouts and decisions.

## Requirements
1. **Safety** – Default fail-open/closed per artifact type; configurable per rollout.
2. **Observability** – Real-time status, SLO checks, regression signals; surfaced via CLI/dashboards.
3. **Recovery** – Automatic rollback on failed stage; manual override supported.
4. **Compatibility** – Honors capability negotiation; skips nodes lacking required features.

## Deliverables
- Rollout service in MeshKit with APIs and CLI verbs.
- Integration with registry metadata and control protocol epochs.
- Dashboards/alerts for rollout health.

## Acceptance Criteria
- Canary -> ramp -> global workflows executed in tests; regression triggers rollback.
- Kill switch disables targeted artifact within bounded time; propagated to agents/OmniRelay.
- Audit entries for each action with user/time/context.

## Testing Strategy
- Integration: staged rollout with synthetic regressions; capability mismatches; kill switch.
- Chaos: control-plane partition during rollout; ensure LKG and rollback behavior are correct.

## References
- `docs/architecture/MeshKit.SRS.md`
- `docs/architecture/OmniRelay.SRS.md`

## Status
Needs re-scope (post-BRD alignment).
