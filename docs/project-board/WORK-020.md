# WORK-020 – Synthetic Probes & Partition Tests

## Goal
Create read-only probes and partition tests to continuously validate control/data paths, capability negotiation, and LKG behavior without impacting production traffic.

## Scope
- Synthetic requests against control APIs and data-plane endpoints (HTTP/gRPC) with downgrade detection.
- Partition simulations: drop control stream, CA outage, registry unavailability; verify agent/OmniRelay responses.
- Probes run as scheduled jobs and CI smoke; results feed alerts (WORK-018).

## Requirements
1. **Non-intrusive** – Probes read-only; tagged; rate-limited per tenant/region.
2. **Coverage** – Central, agent, bridge paths; in-proc/sidecar/edge modes.
3. **Verification** – Check capability negotiation responses, LKG usage, and admin state transitions.
4. **Reporting** – Emit metrics/logs consumable by dashboards/alerts.

## Deliverables
- Probe suite and runner; configs for environments.
- Scenarios for partitions and downgrade detection.
- Documentation for scheduling and interpreting results.

## Acceptance Criteria
- Probes detect regressions in control stream health, cert expiry risk, and downgrade rates.
- Partition simulations confirm LKG fallback and recovery behaviors.

## Testing Strategy
- Automated runs in CI and staging; chaos hooks for partitions.

## References
- `docs/architecture/MeshKit.SRS.md`
- `docs/architecture/OmniRelay.SRS.md`

## Status
Needs re-scope (post-BRD alignment).
