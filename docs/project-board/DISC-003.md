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

## References
- `docs/architecture/service-discovery.md` – “Shard ownership + routing metadata”.
