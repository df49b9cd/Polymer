# Simulation Lab (Multi-Cell at Scale)

Purpose: reproducible, containerized environment to exercise OmniRelay (transport/data plane), MeshKit (discovery), and the control plane across regions/cells/shards with HTTP/3 and Native AOT builds on Alpine.

## Goals
- Model N regions × M cells each with its own control-plane instance, MeshKit ring, and OmniRelay nodes.
- Validate config rollout (canary → region → global), discovery drift, bridge relays, and exportable-service rules.
- Run under CI and developer laptops with resource caps; deterministic seeds for repeatability.

## Topology Model
- **Region**: grouping of cells; own control-plane/bus endpoints.
- **Cell**: local MeshKit gossip ring + OmniRelay pods; optional bridge relays to other cells/regions.
- **Shards/Services**: synthetic workloads tagged with shard ownership and exportability flags.

## Container Composition
- Base images: `mcr.microsoft.com/dotnet/nightly/runtime-deps:10.0-alpine` for Native AOT outputs; publish binaries with `./eng/run-aot-publish.sh linux-x64 Release` and copy into minimal Alpine images.
- Control plane: REST/gRPC + CAS store (sqlite/postgres) + event bus (NATS/Redis) per region.
- MeshKit: registry + gossip + optional bridge relays; per-cell instances.
- OmniRelay nodes: HTTP/3 listeners enabled (QUIC); configured via snapshots; bound to localhost for sidecar-style tests.
- Traffic generators: lightweight Go/Rust/C# clients that replay canned scenarios (canary shift, shadowing, failover) at controlled QPS.

## Orchestration Options
- **docker-compose** for quick starts: template generates services for R regions × C cells × K nodes using parameterized YAML (render via `envsubst` or `yq`).
- **k3d/kind** for closer-to-prod networking: one k8s cluster with namespaces per region; DaemonSets/Deployments for control-plane/MeshKit/OmniRelay; use NodePorts or cluster-local DNS to mimic per-region endpoints.
- **Tilt/Skaffold** optional for live-reload of binaries during dev.

## Traffic & Scenarios
- Steady-state load per service/shard with latency budgets; shadowing toggles; failover drills (bridge down, cell isolated, region outage).
- Config rollout waves: trigger via CLI against control plane and observe drift/resolution.
- Exportable vs non-exportable services: verify bridge allowlists and quotas.
- Certificate/key rotation: push new JWKS/PKI materials, ensure OmniRelay swaps without downtime.

## Failure Injection
- Network partitions: tc/iptables inside bridge relays; k8s NetworkPolicy toggles in k3d/kind.
- Process kills/restarts: `docker kill`/`kubectl delete pod` loops.
- Bus outages: pause NATS/Redis containers to test pull fallback.
- Clock skew: run containers with `libfaketime` for token/TTL edge cases (optional).

## Observability
- OTLP collector (otel-collector) aggregating Envoy/OmniRelay/MeshKit/control-plane telemetry.
- Prometheus + Grafana dashboards for configVersion drift, discoveryGeneration, rollout progress, bridge lag, error rates.
- Loki for structured logs; ensure config diffs are summarized not full docs.

## Reproducibility & CI Hook
- Single command: `./eng/run-sim.sh --regions 2 --cells 2 --nodes 3 --traffic 200rps` builds AOT images, seeds topology, and runs scenarios.
- Deterministic seeds for gossip/traffic; fixtures checked into `samples/simlab/` (topology YAML, workload definitions, assertions).
- CI stage runs a reduced footprint (e.g., 1 region × 1 cell × 2 nodes) to keep time under budget; nightly runs larger matrix.

## Config Artifacts
- **Topology file** (YAML): regions, cells, bridge pairs, exportable services, quotas.
- **Initial snapshots**: per-scope dispatcher configs and rollout plans.
- **Scenario scripts**: shell/PowerShell that mutate control plane and record metrics.

## Key Checks to Automate
- No per-request lock regressions under 1000s of snapshot swaps.
- Drift closes within SLA after bus outage (pull fallback works).
- Bridge allowlist enforcement (non-exportable services never leak across cells).
- Canary/rollback triggers on injected SLO breach.
- Native AOT binaries start under Alpine with HTTP/3 enabled and minimal RSS.
