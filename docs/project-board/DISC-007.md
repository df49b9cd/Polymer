# DISC-007 – Registry Read APIs

## Goal
Expose authoritative peer, cluster, version, and configuration data through versioned control-plane read endpoints with strong RBAC and streaming support.

## Scope
- Build REST + gRPC endpoints:
  - `/control/peers`
  - `/control/clusters`
  - `/control/versions`
  - `/control/config`
- Implement filtering (role, cluster, version, health), pagination, and sorting.
- Provide streaming interfaces (SSE/gRPC) for peers/clusters to power dashboards.
- Generate OpenAPI/Protobuf contracts and publish sample queries.

## Requirements
1. **RBAC** – Enforce scopes (`mesh.read`, `mesh.observe`) and integrate with existing auth providers.
2. **Performance** – Target <200 ms responses for 1k peers; handle 100+ concurrent streaming clients.
3. **Caching** – Support ETag + `If-None-Match` semantics for REST endpoints.
4. **Consistency** – Document snapshot semantics (e.g., data reflects last committed registry version).
5. **Telemetry** – Log/metric each request including negotiated transport/encoding for visibility.

## Deliverables
- API implementations with unit/integration tests.
- Documentation (OpenAPI spec, markdown) showing query examples and permission requirements.
- SDK/CLI helpers for invoking the new endpoints.

## Acceptance Criteria
- Endpoints return accurate data when exercised against a test registry; streaming clients receive updates on change.
- Unauthorized calls fail with 401/403 and audit entries appear.
- CLI `omnirelay mesh peers/clusters` commands consume these APIs with pagination support.

## References
- `docs/architecture/service-discovery.md` – “Discoverable peer registry API”, “Peer/cluster registry APIs”.
