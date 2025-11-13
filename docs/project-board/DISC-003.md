# DISC-003 – Shard Schema & Persistence

## Goal
Design and implement the durable shard ownership store that tracks which mesh node owns each shard/task queue, including history for auditing and deterministic hashing strategies.

## Scope
- Define the shard record schema (`shardId`, `namespace`, `strategy`, `ownerNodeId`, `leaderId`, `capacityHint`, `status`, `version`, `updatedAt`, `changeTicket`).
- Implement a persistence layer (initially Postgres/SQL; abstracted for future etcd/Redis) with optimistic concurrency control.
- Store rebalance history (who changed what, previous owner, diff %) for governance.
- Provide deterministic hash implementations (ring, rendezvous, locality-aware) selectable per namespace with configuration.

## Requirements
1. **Versioning** – Every mutation increments a version + checksum to support client caching and conflict detection.
2. **Auditing** – Persist history rows with actor, reason, and optional change ticket reference.
3. **Strategy plug-ins** – Hash strategies must be pluggable; include unit tests with deterministic fixtures.
4. **Performance** – Target shard lookups <50 ms P99 and support ≥1k shards per cluster.
5. **Data access** – Provide repository APIs for listing shards, fetching by id, and streaming diffs.

## Deliverables
- Schema migration scripts + ORM models.
- Strategy library with ring/rendezvous/locality implementations and configuration binding.
- Automated tests covering hashing determinism, schema constraints, and history recording.
- Documentation describing shard data contracts and strategy selection guidance.

## Acceptance Criteria
- Creating/updating shards records change history and version metadata.
- Hash strategies produce stable results across processes (verified via golden tests).
- Performance tests demonstrate lookup latency and throughput targets.

## Testing Strategy

### Unit tests
- Cover schema models and repositories with optimistic concurrency fixtures so version/checksum bumps are enforced and rejected updates emit descriptive errors.
- Add deterministic golden tests for ring, rendezvous, and locality-aware hash strategies to prove stable assignments when node sets change.
- Validate history/audit builders to ensure actor, reason, and change-ticket metadata are persisted on every mutation.

### Integration tests
- Run database-backed tests (Postgres first) that execute migrations, seed sample shards, and verify CRUD/list/stream-diff APIs under parallel load while maintaining <50 ms lookup latency.
- Simulate concurrent writers via EF/Dapper repos to confirm version conflicts are surfaced and can be retried without corruption.
- Exercise strategy selection via configuration binding to prove namespaces can switch hashing strategies without redeploying code.

### Feature tests

#### OmniRelay.FeatureTests
- Provision namespaces with ring, rendezvous, and locality strategies, trigger rebalances, and confirm CLI/API snapshots plus `omnirelay mesh shards diff` all agree on deterministic ownership.
- Execute a governance workflow that edits shards with change tickets, then verifies audit history exports and compliance checks within the test harness.

#### OmniRelay.HyperscaleFeatureTests
- Load thousands of shards across many namespaces, drive rolling node changes, and ensure hashing strategies rebalance predictably without exceeding latency budgets.
- Re-run governance scenarios under concurrent writers to make sure audit history, versioning, and conflict resolution hold up when multiple operators edit shards simultaneously.

## References
- `docs/architecture/service-discovery.md` – “Shard ownership + routing metadata”.
