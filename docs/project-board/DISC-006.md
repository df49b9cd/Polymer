# DISC-006 – Rebalance Observability Package

## Goal
Deliver dashboards, alerts, and telemetry wiring that make rebalancing activity observable to operators in real time.

## Scope
- Define Prometheus metrics for controller state (`mesh_rebalance_state`, `mesh_rebalance_shards_moving`, `mesh_rebalance_duration_seconds`, `mesh_rebalance_plan_approvals_total`) plus events for drain progress.
- Create Grafana dashboard panels visualizing: active plans, per-node shard counts, drain timelines, approval queue, and failure reasons.
- Author alert rules (Prometheus) for scenarios such as stuck rebalances, excessive concurrent moves, repeated failures, or absence of controller heartbeats.
- Document runbooks referencing dashboard panels and CLI commands for remediation.

## Requirements
1. **Data sources** – Metrics must originate from the controller service and be scrape-ready; include labels for namespace, cluster, plan id.
2. **Dashboards** – Provide both high-level (exec) and deep-dive (on-call) views; include templating for namespace/cluster filters.
3. **Alerts** – Provide recommended thresholds and tuning guidance; integrate with existing alert routing (PagerDuty, Teams, etc.).
4. **Docs** – Update `docs/observability` with dashboard descriptions, installation steps, and screenshot references.

## Deliverables
- Prometheus metric instrumentation + unit tests for label cardinality.
- Grafana dashboard JSON (checked into repo) with versioning instructions.
- Alerting rules YAML + sample configuration.
- Runbook page describing interpretation steps and CLI tie-ins.

## Acceptance Criteria
- Dashboards render correctly against a test environment with both healthy and failing rebalances.
- Alerts trigger during simulated failures and remain quiet during steady-state.
- Documentation vetted by SREs; includes at least one screenshot and workflow example.

## References
- `docs/architecture/service-discovery.md` – “Health-aware rebalancing”, “Observability + operator tooling”.
