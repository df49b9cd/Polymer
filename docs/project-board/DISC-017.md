# DISC-017 – Synthetic Health Checks

## Goal
Deploy automated probes that continuously verify discovery-plane functionality: control APIs availability, leadership rotation, shard watcher responsiveness, and HTTP/3 negotiation.

## Scope
- Implement synthetic check agents (container/cron job) that periodically:
  - Call `/control/peers`, `/control/shards`, `/control/clusters`, `/control/versions` and verify responses/latency.
  - Subscribe to leadership and shard watch streams, validating heartbeat cadence.
  - Perform gRPC calls over HTTP/3 and confirm fallback to HTTP/2 when forced.
- Push results to Prometheus/Grafana and alerting pipelines.

## Requirements
1. **Configurable targets** – Checks can run per cluster/namespace with custom intervals and thresholds.
2. **Isolation** – Probes should run with least privilege (read-only creds) and not depend on production traffic paths.
3. **Alerting** – Integration with alert system when checks fail consecutively or exceed latency budgets.
4. **Reporting** – Generate summarised health reports (daily/weekly) for leadership review.

## Deliverables
- Synthetic checker service/container, helm chart/deployment manifests.
- Alert rules and dashboard widgets showing check status.
- Documentation for deployment, tuning, and troubleshooting.

## Acceptance Criteria
- Synthetic checks detect intentionally injected failures (API downtime, slow responses, stream stall) and raise alerts within configured time.
- Observability dashboards show pass/fail trends; reports automatically generated.
- Checks can be paused/adjusted via config without redeploying code.

## References
- `docs/architecture/service-discovery.md` – “Observability + operator tooling”, “Recommended topology”, “Risks & mitigations”.
