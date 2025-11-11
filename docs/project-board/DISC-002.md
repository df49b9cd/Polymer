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
