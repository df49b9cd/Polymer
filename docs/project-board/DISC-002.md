# DISC-002 – Leadership Service

## Goal
Provide deterministic leader elections (global + per shard) with fenced tokens so only one coordinator issues routing decisions at a time, and emit rich observability for leader transitions.

## Scope
- Implement Raft- or lease-based elections backed by the registry store (etcd/Postgres/Redis abstraction).
- Surface leadership tokens (`leaderId`, `scope`, `term`, `fenceToken`, `expiresAt`) via in-memory APIs and `/control/events/leadership` (SSE + gRPC stream).
- Integrate with the gossip host for membership input and failure detection.
- Publish metrics/logs for leader changes, split-brain detection, and election duration.

## Requirements
1. **Fencing** – Tokens must include monotonically increasing fence numbers; downstream services must reject stale tokens automatically.
2. **Scopes** – Support at least two scopes: `global-control` and per-shard scopes (keyed by namespace + shard id). Additional scopes should require no design change.
3. **Downtime tolerance** – Elections must complete within 5 seconds under nominal conditions; configurable maximum wait for degraded links.
4. **Transport** – Leadership APIs run over gRPC/HTTP/3 + Protobuf with HTTP/2 downgrade; events telemetry must record negotiated transport.
5. **Auditability** – Every leadership change logged with correlation IDs and written to the registry’s audit stream.

## Deliverables
- Leadership service library + background hosted service.
- `/control/events/leadership` endpoints (HTTP stream + gRPC) and documentation.
- Integration tests ensuring exclusive leadership, fencing enforcement, and downgrade behavior.
- Grafana panels displaying current leaders and churn rate.

## Acceptance Criteria
- Under load tests, only one leader per scope issues shard assignments; attempts from stale leaders are rejected.
- Killing the current leader elects a successor within the SLA and emits metrics/logs.
- CLI command `omnirelay mesh leaders status` reports consistent data derived from the new APIs.

## References
- `docs/architecture/service-discovery.md` – “Membership gossip layer + leader elections”, “Routing metadata service”, “Recommended topology”.

## Implementation status (leadership service v1)

- **Code** – `LeadershipCoordinator`, `LeadershipEventHub`, and `InMemoryLeadershipStore` live under `src/OmniRelay/Core/Leadership/`. Coordinators run per-process, watch configured scopes, and drive fenced lease acquisition/renewal via a shared `ILeadershipStore`. `LeadershipServiceCollectionExtensions` wires the pieces through DI and registers the coordinator on the dispatcher lifecycle whenever `mesh:leadership:*` settings are present.
- **APIs** – `/control/leaders` now serves JSON snapshots (with optional `?scope=` filtering) and `/control/events/leadership` streams Server-Sent Events from the same hub. The gRPC inbound hosts `LeadershipControlService.Subscribe` (HTTP/3 with HTTP/2 downgrade) defined in `Core/Leadership/Protos/leadership_control.proto`, so automation can watch the same feed regardless of transport.
- **Telemetry** – Prometheus meters `mesh_leadership_transitions_total`, `mesh_leadership_election_duration_ms`, and `mesh_leadership_split_brain_total` are emitted by `LeadershipMetrics`. Streams log the negotiated HTTP protocol to satisfy the transport-audit requirement, and split-brain detection fires structured warnings plus metrics when incumbents disappear from gossip.
- **CLI + samples** – `omnirelay mesh leaders status` (in `src/OmniRelay.Cli/Program.cs`) calls the new endpoints to print a snapshot or tail the SSE feed. The ResourceLease mesh demo configs now enable `mesh:leadership` with example scopes/shards so the coordinator runs out of the box in dev + prod templates.
- **Tests** – `LeadershipCoordinatorTests` (in `tests/OmniRelay.Core.UnitTests/Leadership/`) exercise multi-node elections and failover fencing using the in-memory store to make sure only one leader issues assignments per scope.
