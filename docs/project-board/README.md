# Project Board Overview

This file lists dependency and parallelization guidance for the DISC-### implementation cards.

## Dependencies

- Foundation: `DISC-001` (Gossip) and `DISC-002` (Leadership) must land before routing metadata, rebalancer, or multi-cluster work begins.
- Registry data: `DISC-003` (Shard schema) precedes `DISC-004` (Shard APIs), `DISC-005` (Rebalancer), and `DISC-013` (Transport policy checks).
- Security: `DISC-009` (Bootstrap) is prerequisite for `DISC-010` (Join tooling), `DISC-013` (Transport governance), and any environment scripts that require mTLS.
- Multi-cluster: `DISC-011` (Cluster descriptors) must complete before `DISC-012` (Replication/failover).
- Observability consumers (DISC-015/017) rely on telemetry emitted by DISC-002/004/005/012.

## Parallelizable Workstreams

- Once `DISC-001`/`002` are underway, teams can begin `DISC-003` (schema) and `DISC-009` (security) in parallel.
- After `DISC-003` is available, `DISC-004`, `DISC-005`, and `DISC-013` can proceed concurrently (with coordination on shared contracts).
- `DISC-006` (Rebalance observability) can build alongside `DISC-005` once metrics are defined.
- `DISC-007` (Registry read APIs) can start as soon as base registry storage is ready; `DISC-008` follows immediately after read path stabilization.
- `DISC-010` (Join tooling) follows `DISC-009` but does not block other control-plane efforts.
- `DISC-012` (Replication/failover) may start once `DISC-011` is done and leadership is stable (`DISC-002`).
- `DISC-014`/`015` (docs + dashboards) can iterate in parallel once upstream APIs/metrics stabilize.
- `DISC-016` (CLI) can track behind the APIs it wraps (`DISC-004`, `DISC-007`, `DISC-008`, `DISC-012`).
- `DISC-017` (synthetic checks) and `DISC-018` (chaos environments) can begin after minimum viable control plane is available (`DISC-001`–`004`).
- `DISC-019` (chaos automation) sits on top of `DISC-018` and matures as more features land.

Refer back to `docs/architecture/service-discovery.md` for detailed requirements per story.
