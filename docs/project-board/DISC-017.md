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

## Testing Strategy

### Unit tests
- Validate probe configuration parsing (targets, intervals, thresholds) and ensure per-cluster overrides are merged deterministically.
- Test scheduler/executor logic so concurrent probes do not exceed resource budgets and pausing/resuming checks updates internal state correctly.
- Cover result classification and reporting builders to confirm failures include latency samples, HTTP status, and remediation hints.

### Integration tests
- Deploy the synthetic checker container against staging clusters to verify control API probes, stream subscriptions, and HTTP/3 + forced HTTP/2 fallbacks all succeed under normal conditions.
- Inject failures (API shutdown, slow responses, stalled streams) using chaos helpers to ensure alerts fire after the configured number of consecutive failures.
- Confirm metrics feed Grafana panels and that alert routing (PagerDuty/Teams) receives the annotated payloads.

### Feature tests

#### OmniRelay.FeatureTests
- Schedule daily/weekly report generation jobs summarizing pass/fail ratios, top offenders, and latency trends, distributing them to leadership for review.
- Run incident rehearsals where probes detect outages, alerts page responders, and pausing/resuming checks via config toggles works without redeploying the agent.

#### OmniRelay.HyperscaleFeatureTests
- Deploy checker fleets per cluster/namespace to validate coordination, failure isolation, and alert fan-out when hundreds of probes run concurrently.
- Stress reporting pipelines by aggregating large volumes of probe data and verifying dashboards plus ticket automation keep up with the scale.
