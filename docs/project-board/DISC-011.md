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

## Testing Strategy

### Unit tests
- Validate schema constraints (unique ids, allowed states, priority ordering, owner metadata) and ensure transition rules reject invalid flows (e.g., `passive` directly to `maintenance`).
- Test descriptor diff/audit builders so every change captures before/after values, actor, ticket references, and annotations.
- Cover policy helpers that compute failover readiness based on descriptors to guarantee downstream automation receives consistent flags.

### Integration tests
- Execute CRUD operations via REST/gRPC APIs against the registry, confirming RBAC enforcement, optimistic concurrency, and immediate propagation to `/control/clusters`.
- Run CLI commands that create/update descriptors, verifying filtering, pagination, and formatting align with documentation.
- Feed descriptors into the failover orchestration harness (DISC-012) to ensure versioned descriptors can be consumed without additional mapping.

### Feature tests

#### OmniRelay.FeatureTests
- Simulate geo-deployment changes by updating priorities and states, confirming dashboards/metrics reflect the topology shift and approvals/audit logs capture governance metadata.
- Submit descriptors missing required owners/change tickets to verify CLI/API validation errors are actionable and audits track the failure.

#### OmniRelay.HyperscaleFeatureTests
- Manage fleets of clusters across many regions, running rolling updates on descriptors to ensure replication, caching, and observability stay current at scale.
- Hammer the APIs with concurrent descriptor edits to validate conflict detection, RBAC enforcement, and audit throughput when many teams coordinate failover data.

## References
- `docs/architecture/service-discovery.md` – “Multi-cluster awareness”, “Implementation backlog item 6”.
