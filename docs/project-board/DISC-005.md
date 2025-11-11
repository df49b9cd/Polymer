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

## References
- `docs/architecture/service-discovery.md` – “Health-aware rebalancing”, “Risks & mitigations”.
