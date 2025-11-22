# WORK-021 – Chaos Automation

## Goal
Automate chaos scenarios covering control-plane partitions, agent loss, extension crashes, and failover triggers to harden OmniRelay and MeshKit.

## Scope
- Scenario catalog with severity/expected outcomes: control partition, bridge outage, extension watchdog trip, registry corruption, CA downtime.
- Automation runner (CI/nightly) targeting staging environments; integration with alerts.
- Reporting: pass/fail with links to telemetry and logs; issue auto-filing optional.

## Requirements
1. **Repeatable** – Deterministic scenarios with seedable randomness; stable results.
2. **Safe** – Runs in staging/sandboxes; guardrails to prevent production impact.
3. **Coverage** – Exercises LKG fallback, rollout abort, failover orchestrations.
4. **Observability** – Capture metrics/logs/traces during chaos; attach to reports.

## Deliverables
- Automation scripts/manifests; scheduler for nightly/weekly runs.
- Documentation and runbooks linked from alerts.

## Acceptance Criteria
- Core scenarios executed nightly; failures block releases or open issues.
- LKG/rollback/failover behaviors verified under chaos.

## Testing Strategy
- Dry-run mode; validation of chaos runners themselves; regression tests on assertions.

## References
- `docs/architecture/MeshKit.BRD.md`
- `docs/architecture/OmniRelay.BRD.md`

## Status
Needs re-scope (post-BRD alignment).
