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
