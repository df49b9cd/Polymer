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

## Testing Strategy

### Unit tests
- Verify validators for filtering, pagination, sorting, and RBAC claims to ensure only supported query combinations reach the repository layer.
- Test caching helpers that compute ETags and honor `If-None-Match`, asserting cache hits skip work and cache misses produce fresh payloads.
- Exercise stream fan-out components so SSE/gRPC watchers handle heartbeats, client disconnects, and maximum subscriber limits.

### Integration tests
- Run REST + gRPC calls against a seeded registry with 1k peers to confirm <200 ms response targets, correct JSON/Protobuf encoding, and transport negotiation telemetry.
- Stand up streaming clients that reconnect mid-flight, validating resume semantics, throttling, and audit logging for subscription lifecycles.
- Execute CLI commands (`peers`, `clusters`, `versions`, `config`) against the hosted endpoints to guarantee pagination, filtering, and auth flows align with documentation.

### Feature tests

#### OmniRelay.FeatureTests
- Script an operator drill where dashboards and CLI watchers observe deliberate peer churn, confirming REST snapshots, SSE streams, and metrics stay aligned.
- Revoke or downgrade scopes mid-session to ensure clients surface actionable RBAC errors, audit logs capture the denial, and cached ETags invalidate cleanly.

#### OmniRelay.HyperscaleFeatureTests
- Run high-volume read workloads (thousands of peers, 100+ streaming clients) while rotating clusters to verify latency targets and streaming heartbeats hold under pressure.
- Execute bulk RBAC changes across many users to confirm audit trails, caching layers, and client retry behavior remain consistent at scale.

## References
- `docs/architecture/service-discovery.md` – “Discoverable peer registry API”, “Peer/cluster registry APIs”.
