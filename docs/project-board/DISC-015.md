# DISC-015 – Operator Dashboards & Alerts

## Goal
Deliver a comprehensive observability pack (Grafana dashboards + Prometheus/OTLP alerts) covering leadership, shard distribution, gossip health, transport negotiation, and replication lag.

## Scope
- Build dashboards with panels for:
  - Current leaders per scope and election history.
  - Shard counts per node, imbalance heatmaps.
  - Gossip RTT, membership size, suspicion rates.
  - Transport/encoding adoption (HTTP/3 vs fallback).
  - Replication lag and cluster states.
- Provide alert rules for leader flaps, gossip failures, shard imbalance, replication lag, and transport downgrade thresholds.
- Include templating for namespace/cluster filters and RBAC-friendly views.

## Requirements
1. **Data sources** – Use standardized Prometheus metrics; ensure label cardinality manageable.
2. **Docs** – Each dashboard must have a README section describing panels, common questions, and remediation steps.
3. **Testing** – Add automated snapshots/tests ensuring dashboards load (via Grafana provisioning tests).
4. **Versioning** – Store dashboard JSON + alert YAML under version control with release notes.

## Deliverables
- Grafana JSON dashboards, Prometheus rule files.
- Synthetic check definitions that validate dashboards/alerts post-deploy.
- Documentation under `docs/observability` linking to each dashboard.

## Acceptance Criteria
- Dashboards visualize data in staging environments; SREs sign off.
- Alerts trigger under simulated failures and respect silence/maintenance windows.
- Synthetic checks pass in CI/CD and after environment rollout.

## References
- `docs/architecture/service-discovery.md` – “Observability + operator tooling”, “Risks & mitigations”.
