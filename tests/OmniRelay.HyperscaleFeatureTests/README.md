# OmniRelay.HyperscaleFeatureTests

Hyperscale validation suite focused on OmniRelay behaving as a distributed RPC fabric across regions, shards, and failover domains.

## Scope
- Model multi-node clusters that include global leaders, regional leaders, and shard replicas running as discrete OmniRelay hosts or containers.
- Exercise control-plane behaviors: leader election, shard rebalancing, replication fan-out, failover, and topology gossip.
- Validate data-plane guarantees under load, including routing consistency, backpressure propagation, and cross-region call semantics.
- Cover chaos and fault-injection stories (network partitions, container restarts, delayed startups) that surface at hyperscale.
- Exclude single-host feature coverage (handled by `OmniRelay.FeatureTests`) and long-running soak/perf workloads (handled by dedicated perf suites).

## Goals
- Provide an executable harness that mirrors production-grade topologies so regressions in leadership/sharding logic are caught before staging.
- Offer composable cluster fixtures that engineering, capacity, and SRE teams can extend with new regions or shard layouts without re-plumbing infrastructure.
- Keep the developer feedback loop reasonable (~10 minutes w/ Docker Desktop) while still enabling heavier nightly/CI runs when needed.

## Reasoning
- OmniRelay’s value emerges when multiple dispatchers cooperate; single-host tests cannot expose coordination bugs (split-brain, inconsistent shard maps, etc.).
- Containerizing clusters with Testcontainers keeps the environment hermetic and reproducible, allowing deterministic chaos experiments.
- Separating the suite ensures day-to-day PR validation stays fast while still providing a high-fidelity gate for scale-specific features.

## Approach
1. **Topology Builders** – Define reusable cluster shapes (e.g., `GlobalLeaderWithRegions`, `ShardRing`, `EdgeFanOut`). Builders describe node roles, persisted volumes, and exposed ports.
2. **Cluster Fixture** – A custom `IAsyncLifetime` fixture spins up the requested topology via Testcontainers (optionally via docker-compose). It tracks node endpoints, admin ports, and injected faults.
3. **Scenario DSL** – Tests express Given/When/Then steps in terms of cluster operations (promote leader, inject latency, replay workload) plus assertions on telemetry, RPC responses, and metadata snapshots.
4. **Observability Hooks** – Each node streams logs/metrics to the test output helper for postmortem and optional golden-file comparisons.
5. **Chaos Utilities** – Helper APIs pause/unpause containers, change network delay, and simulate region isolation so scenarios can validate resilience.

## Setup
- **Prerequisites**: Docker Desktop (or Linux Docker Engine) with >= 6 CPUs / 8 GB RAM for multi-node clusters, and the `Testcontainers.Desktop` extension if you rely on it in CI.
- **Environment Variables**:
  - `OMNIRELAY_HYPERSCALE_CONTAINERS=true` to enable container orchestration (default true).
  - `OMNIRELAY_HYPERSCALE_TOPOLOGY=<name>` to select a default topology when running `dotnet test` locally.
  - `OMNIRELAY_HYPERSCALE_REGISTRY` to point at a private container registry if you publish custom OmniRelay images.
- **Run**:
  ```powershell
  dotnet test tests/OmniRelay.HyperscaleFeatureTests/OmniRelay.HyperscaleFeatureTests.csproj `
    -p:ParallelizeTestCollections=false
  ```
  (disabling collection parallelism avoids oversubscribing Docker when multiple clusters spin up.)

## Best Practices
- **Test behaviors, not internals** – Interact with nodes through public RPCs and management endpoints. Derive leadership/shard info from exported metadata.
- **Keep clusters minimal** – Start with the smallest topology that demonstrates the behavior (e.g., 1 global + 2 regional rather than 5). Larger runs belong in soak tests.
- **Explicit fault windows** – When simulating outages, bound the duration and assert recovery within a timeout so flakes are actionable.
- **Deterministic seeding** – Use fixed shard IDs, peer IDs, and traffic replay files stored under `Topologies/Seeds/` to make failures reproducible.
- **Fail fast on setup** – Validate Docker daemon availability and required environment variables before starting containers; skip with a clear message if unmet.
- **Capture artifacts** – Dump shard maps, elected leaders, and routing tables as JSON per scenario to aid diffing and regression tracking.
- **Isolate resource usage** – Tag containers with the topology/test name and clean them in `DisposeAsync` to avoid orphaned resources.

## Scenario Ideas
| Category | Example Scenarios |
| --- | --- |
| Leadership | Promote/demote global leader, detect split brain, ensure watchers converge |
| Sharding | Hot shard rebalancing, consistent hashing drift detection, shard stickiness under failover |
| Regional Routing | Geo-affinity routing with fallback, latency-aware routing when a region degrades |
| Chaos | Network partition between global and region leaders, mass shard node restart, delayed bootstrap |
| Telemetry | Ensure leadership changes emit events, shard counters remain monotonic across instances |

## Example Flow
1. `Given` a `GlobalLeaderWithTwoRegions` topology.
2. `When` we pause the global leader container for 15 seconds and promote the west region.
3. `Then` RPCs targeting the global endpoint should route through the new leader, shard ownership should match the new configuration, and metrics should record a leadership change event.

Translate the above into a test by:
```csharp
await cluster.GlobalLeader.PauseAsync(TimeSpan.FromSeconds(15));
await cluster.Regions["west"].PromoteAsync();
var result = await rpcClient.InvokeAsync(...);
result.Should().MatchSnapshot("west_failover.json");
```

## Next Steps
- Flesh out `Topologies/` with actual builder implementations (Docker Compose specs, container factories).
- Implement a `HyperscaleClusterFixture` that scenarios can consume via `[CollectionDefinition]`.
- Seed initial scenarios for leader promotion, shard rebalancing, and partition healing, and wire the suite into a nightly CI workflow.
