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

## Testing Strategy

### Unit tests
- Cover request validators and pagination/filter builders so `/control/shards` enforces namespaces, owners, statuses, and search semantics before hitting the data store.
- Exercise watch resume-token helpers to guarantee reconnects pick up from the last delivered version without duplicating updates.
- Add CLI-focused tests (golden files) for `list`, `diff`, and `simulate` commands in both JSON and table formats to lock output contracts and validation messaging.

### Integration tests
- Stand up the API against the shard store and issue REST + gRPC requests under parallel load, confirming RBAC scopes, ETag caching, and <200 ms P95 latency for 1k shards.
- Run long-lived SSE/gRPC watcher sessions that drop connections midstream, verifying resume tokens, backoff, and metrics for subscriber counts.
- Execute CLI commands against the hosted API to ensure diff/simulate flows stream progress, honor `--namespace`, and exit non-zero on invalid strategies or auth failures.

### Feature tests

#### OmniRelay.FeatureTests
- Script an operator workflow that lists shards, requests diffs between versions, runs simulations with custom node sets, and validates rendered plans plus CLI output in the standard feature harness.
- Restart control-plane components mid-run to ensure SSE/gRPC watchers resubscribe automatically, resume with the correct token, and keep dashboards in sync.

#### OmniRelay.HyperscaleFeatureTests
- Flood the APIs with wide filters/pagination across thousands of shards while multiple operators stream updates, ensuring latency, RBAC enforcement, and CLI usability remain within targets.
- Perform failover drills where dozens of watchers reconnect simultaneously after an upgrade, confirming resume tokens prevent gaps and that audit logs capture the surge.

## References
- `docs/architecture/service-discovery.md` – “Shard ownership + routing metadata”, “Peer/cluster registry APIs”.
