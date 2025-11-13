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

## Testing Strategy

### Unit tests
- Validate dashboard JSON and alert YAML via schema/tooling (`grizzly`, `tanka`, or Grafana JSON lint) so every commit passes structure and datasource checks.
- Cover helper libraries that compute derived metrics (e.g., shard imbalance heatmaps, transport downgrade ratios) to ensure calculations match documented formulas.
- Test synthetic check definitions to verify probe intervals, timeout thresholds, and label conventions remain consistent.

### Integration tests
- Provision dashboards in a staging Grafana connected to real Prometheus data, capturing screenshot diffs or panel JSON snapshots to detect regressions.
- Run Prometheus alert unit tests/simulations that replay sample metrics for leader flaps, gossip failures, shard imbalance, and replication lag to ensure correct firing behavior.
- Execute synthetic health checks end to end, verifying they write status metrics, raise alerts, and feed the operator overview panels.

### Feature tests

#### OmniRelay.FeatureTests
- Orchestrate SRE game days injecting leadership churn, gossip degradation, and transport downgrades, confirming dashboards guide remediation and alerts escalate appropriately.
- Conduct acceptance walkthroughs with exec, on-call, and operator personas to verify templating and RBAC-filtered panels surface the expected detail for each audience.

#### OmniRelay.HyperscaleFeatureTests
- Replay simultaneous incidents across multiple clusters to test dashboard performance, alert deduplication, and silence handling when signal volume spikes.
- Validate export/report tooling by generating organization-wide snapshots from dozens of dashboards, ensuring links, screenshots, and annotations remain coherent at scale.
