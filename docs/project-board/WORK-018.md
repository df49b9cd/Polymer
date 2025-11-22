# WORK-018 – Dashboards & Alerts

## Goal
Provide operator dashboards and alerting that cover MeshKit control-plane health, OmniRelay data-plane health, extension rollouts, and capability/epoch states.

## Scope
- Dashboards: control streams (lag, errors), identity/CA status, agent health, rollout status, extension watchdogs, data-plane latency/error rates per mode.
- Alerts: SLO breaches, rollout regressions, CA expiry risk, capability mismatches, LKG fallback events, watchdog terminations.
- Templates for Grafana/Prometheus (or chosen stack) with documented signals.

## Requirements
1. **Correlation** – Visualize metrics by config epoch and rollout stage.
2. **Actionability** – Alerts include remediation hints and links to CLI commands.
3. **Coverage** – Signals from central, agent, bridge, and all OmniRelay modes.
4. **Performance** – Dashboards load within target time; queries optimized.

## Deliverables
- Dashboard JSON/templates and alert rules.
- Runbook snippets linked from alerts.
- Docs describing signals and ownership.

## Acceptance Criteria
- Dashboards validated against staging data; alerts fire in synthetic regressions.
- Alert noise tuned (low false positives) with rate limits where needed.

## Testing Strategy
- Synthetic events to trigger each alert; visual regression checks for dashboards.

## References
- `docs/architecture/MeshKit.SRS.md`
- `docs/architecture/OmniRelay.SRS.md`

## Status
Needs re-scope (post-BRD alignment).
