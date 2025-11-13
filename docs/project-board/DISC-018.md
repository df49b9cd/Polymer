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

## Testing Strategy

### Unit tests
- Validate scenario definitions and fault-injection helpers (tc/netem wrappers, chaos container drivers) via schema tests and dry-run previews to ensure parameters are parsed correctly.
- Add script-level tests for provisioning/teardown commands to guarantee environment variables, directory layouts, and cleanup sequences behave on macOS/Linux CI runners.
- Test log/metric collection utilities so artifact packaging always includes timestamps, cluster ids, and scenario metadata for later analysis.

### Integration tests
- Stand up the docker-compose and Kubernetes chaos stacks in CI, verifying OmniRelay nodes, registry, and observability components all bootstrap successfully before faults are injected.
- Execute sample fault chains (leader kill, network partition, latency spike, cert expiration) and confirm orchestration scripts apply the desired tc/netem settings and revert them afterward.
- Validate collected artifacts (logs, metrics snapshots, topology dumps) are uploaded to the expected storage bucket and referenced in the run summary.

### Feature tests

#### OmniRelay.FeatureTests
- Run documented chaos scenarios as regular game days, ensuring engineers follow playbooks to inject faults, monitor dashboards, and capture findings for retrospectives.
- Replay prior incidents to verify chaos helpers faithfully reproduce the conditions and that cleanup scripts reset the environment for the next run.

#### OmniRelay.HyperscaleFeatureTests
- Execute chained fault experiments (network partitions, leader kills, cert expirations) across large node counts to validate orchestration reliability and observability at scale.
- Stress artifact collection by capturing high-volume logs/metrics/checkpoints from extended runs, confirming archival pipelines and storage quotas keep pace.
