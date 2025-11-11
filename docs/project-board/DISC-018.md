# DISC-018 – Chaos Test Environments

## Goal
Provision reproducible multi-node environments (docker-compose and Kubernetes) for executing scripted chaos experiments targeting the discovery plane.

## Scope
- Create docker-compose stack spinning up multiple OmniRelay nodes, registry, Prometheus, Grafana, and fault-injection helpers (tc/netem, chaos containers).
- Provide Kubernetes manifests/Helm charts replicating the setup with configurable node counts and namespaces.
- Include scripts to trigger faults: kill leaders, partition network segments, inject latency/packet loss, surge membership joins/leaves, simulate cert expiration.
- Package data collection (logs, metrics, checkpoints) for post-analysis.

## Requirements
1. **Determinism** – Environments must be reproducible via single command; document prerequisites.
2. **Fault library** – Parameterize faults (duration, intensity) and chain multiple faults in a scenario.
3. **Telemetry** – Ensure observability stack collects all necessary metrics/logs by default.
4. **Cleanup** – Provide teardown scripts to clean resources and avoid leftover state.

## Deliverables
- `docker-compose.chaos.yml`, Kubernetes manifests, helper scripts.
- Documentation describing setup, fault catalog, and safety guidelines.
- Sample scenarios demonstrating usage.

## Acceptance Criteria
- Engineers can start the chaos environment locally/in cluster, run a sample scenario, and collect artifacts following documentation.
- Observability dashboards function within the chaos environment.
- Fault scripts support at least: leader kill, network partition, latency injection, membership surge.

## References
- `docs/architecture/service-discovery.md` – “Testing & chaos validation”, “Implementation backlog item 9”.
