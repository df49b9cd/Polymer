# DISC-011 – Multi-Cluster Descriptors

## Goal
Introduce first-class cluster metadata (state, priority, replication endpoints) to support geo-redundant deployments and future failover automation.

## Scope
- Define cluster descriptor schema (`clusterId`, `region`, `state`, `priority`, `failoverPolicy`, `replicationEndpoints`, `owners`, `changeTicket`, `annotations`).
- Persist descriptors in the registry with CRUD APIs and audit history.
- Extend `/control/clusters` endpoints + CLI to list, filter, and inspect descriptors.
- Add validation (unique ids, allowed states, priority rules).

## Requirements
1. **States** – Support at least `active`, `passive`, `draining`, `maintenance`; include transition rules.
2. **Failover metadata** – Track planned vs emergency failover flags, version vectors, and dependencies.
3. **Governance** – Require owner/team metadata + change-ticket references for updates.
4. **Observability** – Metrics exposing cluster counts per state and failover readiness; dashboards showing topology.

## Deliverables
- Schema migrations/models, API handlers, CLI commands.
- Documentation for cluster lifecycle management and policy guidance.
- Integration tests verifying validation rules and RBAC.

## Acceptance Criteria
- Operators can create/update cluster descriptors via API/CLI with full validation and audit logs.
- `/control/clusters` reflects state changes immediately; dashboards display new metadata.
- Failover automation (DISC-012) can consume descriptors for orchestration decisions.

## References
- `docs/architecture/service-discovery.md` – “Multi-cluster awareness”, “Implementation backlog item 6”.
