# DISC-005 – Health-Aware Rebalancer

## Goal
Create the automated controller that evaluates mesh health/metrics and reassigns shards responsibly, including dry-run and approval flows.

## Scope
- Service that ingests telemetry (pending depth, latency, error rate) and gossip health snapshots to score nodes.
- Decision engine implementing state machine (`steady`, `investigating`, `draining`, `rebalancePending`, `rebalancing`, `completed`, `failed`).
- Policy configuration per namespace/cluster specifying thresholds, cooldowns, maximum concurrent moves.
- Dry-run + approval APIs (`/control/rebalance-plans`) allowing operators to review before execution.

## Requirements
1. **Input adapters** – Pull metrics via OpenTelemetry/Prometheus scrapes or direct feed; support pluggable adapters.
2. **Safety** – Respect guardrails: cap number of shards moved simultaneously, ensure at least one healthy replica remains, and throttle rebalances during ongoing incidents.
3. **Notifications** – Emit events/logs/alerts when entering/exiting states; integrate with on-call channels.
4. **Auditability** – Persist executed plans with before/after snapshot, actor, and change ticket references.
5. **Transport** – Controller APIs run on gRPC/HTTP3 + Protobuf w/ HTTP2 fallback; CLI integration uses same endpoints.

## Deliverables
- Controller service + configuration schema.
- REST/gRPC endpoints for listing plans, approving, cancelling, and monitoring progress.
- Grafana panels showing current rebalances, queue lengths, and policy thresholds.
- Integration tests simulating unhealthy nodes and verifying safe reassignment.

## Acceptance Criteria
- Triggering simulated failures causes controller to schedule rebalances within policy.
- Dry-run generates previews operators can review; approved plans execute and emit completion events.
- Metrics/alerts fire when controller exceeds thresholds or encounters errors.

## Testing Strategy

### Unit tests
- Validate the scoring engine across combinations of latency, error rate, and backlog inputs to ensure the state machine transitions (`steady`→`investigating`→`draining`…) follow policy.
- Assert guardrails (max shards in flight, healthy replica requirement, cooldown timers) are honored even when plans are queued back-to-back.
- Cover dry-run builders so generated plans capture before/after owners, policy metadata, and ticket fields used for approvals.

### Integration tests
- Feed synthetic telemetry via OpenTelemetry/Prometheus adapters while running the controller service to verify plan creation, approval APIs, and retry behavior under transient registry failures.
- Exercise gRPC/HTTP3 endpoints (`/control/rebalance-plans`) end to end via CLI to ensure RBAC scopes, pagination, and streaming updates behave consistently.
- Simulate unhealthy nodes in the mesh demo to confirm the controller ingests gossip health, emits metrics (`mesh_rebalance_*`), and publishes notifications during each lifecycle state.

### Feature tests

#### OmniRelay.FeatureTests
- Run scripted scenarios that trigger dry-run plans, collect approval, execute the rebalance, and validate shard ownership plus alert streams all reflect safe completion.
- Inject policy violations or concurrent incidents to ensure the controller throttles actions, emits warnings, and blocks execution until an override is acknowledged.

#### OmniRelay.HyperscaleFeatureTests
- Simulate multi-namespace clusters with hundreds of shards moving simultaneously, confirming guardrails cap concurrent moves and that telemetry stays within noise budgets.
- Stress-test dry-run/approval queues under parallel operator actions to verify ordering, audit trails, and rollback hooks behave at large scale.

## References
- `docs/architecture/service-discovery.md` – “Health-aware rebalancing”, “Risks & mitigations”.
