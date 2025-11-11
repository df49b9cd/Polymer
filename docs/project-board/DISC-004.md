# DISC-004 – Shard APIs & Tooling

## Goal
Expose shard ownership data through control-plane APIs/streams and provide CLI utilities to inspect, diff, and simulate shard assignments.

## Scope
- Build `/control/shards` REST + gRPC endpoints with filtering (namespace, owner, status), pagination, search by shard id, and ETag/version headers.
- Implement watch streams (SSE + gRPC) that push shard updates to subscribers with backoff + resume tokens.
- Add CLI commands:
  - `omnirelay mesh shards list --namespace foo`
  - `omnirelay mesh shards diff --from-version X --to-version Y`
  - `omnirelay mesh shards simulate --strategy rendezvous --nodes n1,n2,...`
- Add API docs/OpenAPI schemas and examples.

## Requirements
1. **Security/RBAC** – Enforce `mesh.read` scope for read endpoints; require `mesh.operate` for diff/simulate features if they expose sensitive data.
2. **Performance** – API must return 95th percentile responses <200 ms for 1k shards; watchers should handle at least 100 subscribers per node.
3. **Resilience** – Watch streams support resume tokens so clients can recover after disconnect without missing updates.
4. **CLI UX** – Commands must support JSON and table output, include progress indicators for simulations, and exit non-zero on validation errors.

## Deliverables
- API controllers/handlers + Protobuf definitions.
- CLI implementation with integration tests hitting the live API (fixture).
- Documentation/examples under `docs/reference/diagnostics.md`.

## Acceptance Criteria
- API responses validated via OpenAPI contract tests; watchers verified with integration harness.
- CLI commands operate end-to-end against a test cluster and produce human-readable output.
- Security tests confirm RBAC enforcement and proper HTTP status codes for unauthorized requests.

## References
- `docs/architecture/service-discovery.md` – “Shard ownership + routing metadata”, “Peer/cluster registry APIs”.
