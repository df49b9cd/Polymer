# DISC-012 – Cross-Cluster Replication & Failover

## Goal
Stream replication logs between clusters with version vectors, monitor lag, and orchestrate planned/emergency failovers using the new cluster descriptors.

## Scope
- Enhance replication services to publish events to remote clusters with attached `ClusterVersionVector` metadata.
- Implement lag monitoring (`mesh_replication_lag_seconds`, `mesh_replication_status`) and expose via `/control/clusters`.
- Build failover automation:
  - Planned failover workflow (drain active cluster, promote passive, update routing metadata atomically).
  - Emergency failover workflow (force promote with fencing tokens, optional data loss warnings).
- Provide CLI + runbooks for activation/failback.

## Requirements
1. **Ordering** – Maintain monotonic sequence per cluster; handle idempotent replays and deduplication.
2. **Security** – Replication channels use gRPC/HTTP3 + Protobuf with mTLS and optional mutual attestation.
3. **Automation** – Workflows integrate with change-management approvals; include preflight checks (lag, health).
4. **Observability** – Dashboards show lag, active/passive state, failover progress; alerts fire when lag exceeds thresholds.
5. **Testing** – Chaos scenarios simulate regional loss and verify failover times (<30s for mesh APIs).

## Deliverables
- Replicator enhancements, lag metrics, control APIs for failover commands.
- CLI commands: `omnirelay mesh clusters promote`, `... failback`, `... replication status`.
- Runbooks + diagrams detailing steps, preconditions, rollback.
- Integration tests (multi-cluster) validating planned/emergency workflows.

## Acceptance Criteria
- Replication lag metrics accurate within ±5%; dashboards/alerts wired.
- Planned failover completes within documented SLO, updates routing metadata, and logs audit trail.
- Emergency failover forcibly promotes passive cluster with fencing tokens; clients resume operations within target window.

## References
- `docs/architecture/service-discovery.md` – “Multi-cluster awareness”, “Implementation backlog item 6”.

## Testing Strategy

### Unit tests
- Cover version-vector math (merge, compare, increment) plus deduplication logic to guarantee monotonic ordering and safe replays across clusters.
- Validate workflow state machines for planned vs emergency failovers, ensuring guardrails (preflight checks, approvals, cooldowns) fire in the right order.
- Test serialization layers for replication events so protobuf contracts remain backward compatible and embed the necessary cluster metadata.

### Integration tests
- Spin up two clusters with real replication channels to measure lag metrics, simulate packet loss, and verify `/control/clusters` exposes `mesh_replication_*` data accurately.
- Execute planned failovers end to end: drain primary, promote secondary, update routing metadata, and confirm clients reconnect without stale fencing tokens.
- Inject chaos (region kill, store lag) to drive emergency workflows and ensure alerts, audit records, and CLI outputs match documentation.

### Feature tests

#### OmniRelay.FeatureTests
- Run a runbook-driven drill where SREs execute planned failover, capture metrics/logs, and verify the system returns to steady state before triggering failback.
- Automate nightly regression suites that promote/demote clusters, confirming SLO adherence (<30 s) and zero data divergence across the standard topology.

#### OmniRelay.HyperscaleFeatureTests
- Execute concurrent planned failovers across multiple cluster pairs to validate orchestration, approvals, and telemetry when many regions participate.
- Simulate emergency failovers during replication lag spikes to ensure fencing tokens, automation hooks, and dashboards scale with global traffic volumes.
