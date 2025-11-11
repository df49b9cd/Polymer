# Service Discovery + Peering Gaps

OmniRelay should own the connectivity fabric so product services stay focused on business logic (workflows, catalog updates, etc.). The features below describe what the mesh layer must deliver regardless of which domain-specific service rides on top.

This architecture note lives alongside `docs/architecture/omnirelay-rpc-mesh.md` and expands on the discovery and peering requirements outlined there.

Each subsection outlines the feature, why the mesh needs it, and objective success criteria. The following cross-cutting sections translate those needs into requirements, use-cases, proposals, and a staged plan.

## Functional requirements

1. Automatic peer discovery, health dissemination, and leadership assignment without manual configuration in application services.
2. Deterministic routing metadata (shards, queues, clusters) exposed through stable OmniRelay APIs/SDKs.
3. Self-healing behavior: failed or degraded nodes trigger rebalancing and leadership changes with minimal operator input.
4. Secure enrollment ensuring only authenticated/authorized peers participate in gossip or receive routing data.
5. Cross-cluster awareness with controllable failover priorities and replication flows.
6. Operator accessibility via registry APIs, CLI, dashboards, and alerting that are consistent across teams.
7. Continuous validation through automated chaos/testing harnesses prior to release.

## Non-functional requirements

- **Reliability**: ≥99.9% availability for discovery APIs and gossip under normal load; automatic leader failover <5 seconds.
- **Scalability**: Support ≥200 nodes per cluster and ≥1,000 shards with configuration-based tiering when limits are reached.
- **Security**: Mandatory mTLS for mesh channels, RBAC on control APIs, audit logging for join/leave/leadership events.
- **Performance**: Node join/leave propagation <2 seconds median; shard lookups <50 ms P99.
- **Operability**: Metrics/alerts for gossip RTT, leader flaps, shard imbalance, and replication lag paired with documented runbooks.
- **Compatibility**: Mixed-version operation (N/N-1) with feature gating and upgrade sequencing guidance.

## Core use-cases

1. **Scale-out onboarding** – Add multiple worker nodes and have them appear in routing tables automatically within seconds, no app config changes required.
2. **Failure & recovery** – Lose a routing-capable node; the mesh reassigns shards and clients continue making RPC calls without human intervention.
3. **Multi-region failover** – Promote a standby cluster after simulating regional failure; client SDKs switch endpoints with <30s disruption.
4. **Observability & audits** – SREs query `/control/peers` or dashboards to diagnose skew, then use CLI to drain/rebalance shards.
5. **Secure onboarding** – A new service acquires a signed cert/token and joins the mesh without embedding custom ACL logic.
6. **Upgrade rollout** – Deploy a new OmniRelay version while older nodes stay online; discovery remains stable due to version negotiation.

## Proposed approach

- Build a **gossip core** (memberlist/Ringpop derivative) embedded in OmniRelay host packages, exposing hooks for health and metadata.
- Layer a **consensus-backed leadership service** (Raft or lease-based) for global + shard leaders tied into PeerLeaseHealthTracker.
- Create a **control-plane API surface** (`/control/peers`, `/control/shards`, `/control/clusters`) backed by persisted registry state.
- Extend the **OmniRelay CLI + Grafana dashboards** to visualize and operate the mesh.
- Implement **secure bootstrap** via mutual TLS with pluggable identity providers (SPIFFE, cloud-managed identities).
- Stand up a **chaos/test harness** to simulate churn, partitions, and upgrades.

## Delivery plan

1. **Milestone 1 – Foundation**
   - Prototype gossip membership with health propagation.
   - Expose basic peer list API + CLI commands.
   - Ship initial metrics and dashboard panels.
2. **Milestone 2 – Leadership & routing**
   - Implement leader elections, shard maps, deterministic routing metadata.
   - Add health-aware rebalancing loop and state convergence logic.
   - Publish runbooks and success metrics.
3. **Milestone 3 – Security & multi-cluster**
   - Integrate mTLS/ACL bootstrap, cluster identifiers, replication metadata, and failover workflows.
   - Expand registry API for cluster state + replication lag.
   - Add chaos tests covering cross-cluster failover.
4. **Milestone 4 – Hardening & scale**
   - Stress-test scalability limits, introduce tiered gossip, finalize upgrade/compat strategies.
   - Automate nightly chaos suite + alerting.
   - Release operator guides and service-agnostic integration checklist.

## Required refactorings across OmniRelay subsystems

- **Dispatcher & host builders**
  - Extract membership/join logic into a reusable host module so every OmniRelay process (dispatchers, gateways, background services) automatically participates in gossip without bespoke wiring.
  - Update sample hosts (ResourceLease Mesh Demo, Quickstart servers, etc.) to consume routing metadata from the mesh instead of static URLs so guidance stays consistent across documentation.
- **Peer health & leasing components**
  - Extend `PeerLeaseHealthTracker` and `PeerListCoordinator` to ingest mesh-wide health signals and leadership info; decouple them from ResourceLease-specific code so other services can leverage the same health model.
  - Introduce shard-aware peer choosers that subscribe to the routing tables.
- **Control-plane APIs**
  - Add new controller endpoints (`/control/peers`, `/control/shards`, `/control/clusters`) to OmniRelay’s diagnostics/control subsystem, including pagination, filtering, and RBAC guards.
  - Consolidate existing diagnostics routes under a versioned API surface and ensure both HTTP and gRPC transports expose the same data.
- **Security & bootstrap**
  - Update hosting boilerplate to require mTLS for intra-mesh calls; integrate certificate management (SPIFFE/SPIRE or workload identity) into the OmniRelay configuration layer.
  - Provide tooling (`omnirelay cert issue`, `omnirelay mesh join`) to mint/join nodes securely.
- **Observability stack**
  - Expand OpenTelemetry instrumentation to emit gossip/leadership/shard metrics; ship Grafana dashboards and Prometheus rules that consume the new signals.
  - Ensure log schemas include correlation IDs for membership events to aid auditing.
- **CLI & developer tooling**
  - Enhance `omnirelay mesh` commands to interact with the registry API (list peers, leaders, shards, trigger drains).
  - Update sample READMEs and docker assets so developers use the new mesh features instead of static compose wiring.
- **Testing infrastructure**
  - Build integration fixtures that spin up multi-node OmniRelay clusters (possibly via docker-compose) to run the chaos suite.
  - Extend unit/integration tests for peer choosers, health trackers, and control-plane endpoints to cover versioning and failure scenarios.

## 1. Membership gossip layer + leader elections

- **Feature**: Embed a Ringpop/memberlist-style gossip transport so every OmniRelay node (edge gateways, schedulers, workers, domain services) automatically exchanges membership, health, and metadata. On top of that, run lightweight elections (Raft/lease or SWIM+fencing) to select a global control leader and shard-specific leaders inside the mesh plane.
- **Reasoning**: Static URLs make scaling brittle and leak infrastructure concerns into business services. Gossip lets the mesh self-heal and converge quickly, keeping discovery transparent to applications. Leader leases avoid split-brain when changing shard ownership, dialing backpressure knobs, or orchestrating failover across any hosted service.
- **Success criteria**:
  - Node joins propagate to 95% of peers < 2s; departures trigger eviction and health updates.
  - Leader transitions are observable (Prometheus counter + log) and take < 5s; only one leader issues shard assignments at a time (verified via fencing token).
  - Removing any routing-capable node triggers automatic leader failover and other nodes resume work without manual restarts.

## 2. Shard ownership + routing metadata

- **Feature**: Maintain consistent-hash or rendezvous maps that describe which mesh node owns each shard/task queue, version them, and expose via RPC/HTTP (`/control/shards`) plus watch streams.
- **Reasoning**: Client SDKs, peer choosers, and observability layers need deterministic routing regardless of the business workload. Without ownership metadata, the mesh would thrash or double-deliver messages, forcing application teams to handle routing themselves.
- **Success criteria**:
  - RPC exposes shard table with version + checksum; peer choosers can fetch and cache it.
  - During reshards the delta is < 10% of shards unless forced (keeps hot queues stable).
  - CLI tooling can list which mesh node owns shard `N` and the owning leader; synthetic tests show zero duplicate deliveries after a reshard event.

## 3. Health-aware rebalancing

- **Feature**: Periodic controller loop that ingests gossip health, workload metrics, and queue depth to decide when to pause shards or reassign them. Publish state transitions (`steady`, `draining`, `rebalancing`).
- **Reasoning**: Application teams shouldn’t babysit node failures. The mesh must automatically move shards off unhealthy hosts so top-layer services continue processing without manual routing tweaks, mirroring managed platforms like Cadence.
- **Success criteria**:
  - When a processing node misses heartbeats for N seconds, the loop parks its shards and reassigns within 2 lease intervals.
  - Rebalance events raise structured metrics/logs so Grafana can show “Shard 12 draining from node-a to node-b”.
  - End-to-end request/lease latency stays within ±20% during planned failovers on synthetic load.

## 4. Discoverable peer registry API

- **Feature**: Control-plane API (`/control/peers`, `/control/clusters`) returning JSON/stream updates about peers, roles, health, leadership, and advertised endpoints. Include filters, pagination, and watch semantics.
- **Reasoning**: Domain teams and automation need a service-agnostic inventory to interrogate the mesh. A registry decouples product code from infrastructure endpoints and enables tooling/portals to show live topology.
- **Success criteria**:
  - API returns consistent snapshots < 200ms and supports ETag/version to detect drift.
  - CLI command `omnirelay mesh peers list` can rely solely on the API.
  - Registry enforces RBAC/mTLS so only authorized callers can read/write peer metadata.

## 5. Multi-cluster awareness

- **Feature**: Define cluster IDs, replication streams, and failover priorities. Gossip must propagate cluster info; leaders advertise cross-cluster endpoints and version vectors (similar to Cadence global domains).
- **Reasoning**: Services expect the mesh to provide geo-redundancy. Without cluster metadata, they would have to hardcode regions or run their own replication. Keeping this logic in the mesh keeps domain code portable.
- **Success criteria**:
  - Clusters declare `state=active|passive|draining` and publish monotonically increasing failover versions.
  - Mesh replication services stream logs to followers; lag metrics stay < configurable threshold.
  - Killing the active cluster while promoting the passive one results in < 30s downtime for externally visible mesh APIs.

## 6. Secure peer bootstrap

- **Feature**: TLS mutual auth + ACLs for gossip and control-plane endpoints. Provide bootstrap tokens or signed certificates so only authorized nodes participate. Optionally integrate with SPIFFE/SPIRE or cloud identity.
- **Reasoning**: Infrastructure security should be uniform regardless of which service uses OmniRelay. Hardening the mesh frees application code from embedding bespoke ACL logic.
- **Success criteria**:
  - Peers without valid certs or tokens fail to join; audit logs capture the attempt.
  - Certificate rotation happens without dropping more than 1 gossip RTT.
  - Security posture documented (threat model + hardening guide) and validated via automated tests.

## 7. State convergence + conflict resolution

- **Feature**: Add vector clocks or monotonic versioning for shard maps, leadership, and peer metadata plus background reconciliation tasks that repair divergence after network partitions.
- **Reasoning**: Gossip can temporarily fork state; without conflict resolution stale leaders could keep serving traffic. Versioned metadata and reconciliation ensure deterministic, eventually consistent outcomes.
- **Success criteria**:
  - After a simulated partition, all peers converge to the same shard map version within 2× gossip interval.
  - Conflicting leadership claims trigger automated fencing (losing leader steps down) and emit alerts.
  - Registry garbage-collects tombstoned peers automatically, keeping stale entries < 1%.

## 8. Scalability & tiered topology

- **Feature**: Document and enforce limits (max peers per cluster, shard count, gossip fanout). Support hierarchical gossip (regional supervisors) when exceeding thresholds.
- **Reasoning**: Mesh may grow beyond lab scale; knowing the safe envelope avoids pathological fanout or gossip storms.
- **Success criteria**:
  - Load tests show stable convergence up to target node count (e.g., 200 peers/cluster) with CPU < 30%.
  - Beyond the threshold, operators can enable tiered gossip (region leaders) via config and see traffic drop proportionally.
  - Metrics expose gossip queue length/fanout so alerts fire before saturation.

## 9. Upgrade & compatibility strategy

- **Feature**: Define protocol versions for gossip messages, shard schemas, and APIs. Include feature flags/rollout bits so mixed-version clusters operate safely during upgrades.
- **Reasoning**: Without compatibility guarantees, rolling upgrades could disrupt discovery. Version negotiation keeps older nodes functional until the fleet is updated.
- **Success criteria**:
  - Mixed-version chaos tests (N-1) run green; unsupported combinations are rejected with clear logs.
  - Upgrade guide documents sequencing (control plane first, data plane second) and automated health gates.
  - Feature flags allow canarying new discovery features with <10% of nodes before global rollout.

## 10. Operator tooling & observability

- **Feature**: Ship CLI commands, dashboards, and alerting rules dedicated to discovery/peering (leader status, shard skew, gossip latency).
- **Reasoning**: APIs alone are insufficient; operators need curated UX to debug incidents quickly.
- **Success criteria**:
  - Grafana dashboard visualizes leadership, shard distribution, gossip RTT, and error budgets.
  - `omnirelay mesh` CLI supports `peers list`, `leaders status`, `shards rebalance`, with auth via existing credentials.
  - Alert rules (Prometheus) cover leader flaps, shard imbalance, gossip failure rate > threshold.

## 11. Testing & chaos validation

- **Feature**: Create automated scenarios (integration suite + chaos harness) that kill leaders, partition networks, spike membership churn, and measure whether discovery meets SLAs.
- **Reasoning**: Success metrics need continuous verification; chaos drills prevent regressions.
- **Success criteria**:
  - CI job runs nightly chaos suite and publishes convergence metrics; failures block release.
  - Synthetic load generator demonstrates steady throughput/backpressure while chaos is active.
  - Postmortem templates capture discovery metrics so learnings feed back into design.

## Recommended topology

- Combine the gossip mesh with consensus-backed leader leases. Gossip offers resilient membership and health dissemination; leadership (global + shard-level) keeps configuration, shard assignment, and cross-cluster routing consistent.
- Ensure leaders publish their view of shard ownership so peer choosers and clients route deterministically, and emit Prometheus/Grafana signals for leader elections to aid debugging.
- Keep the control plane logically centralized (one truth for policy) but physically distributed by running the leader elections over multiple nodes—this balances governance with failure tolerance. Success means operators can kill any single node (including the current leader) without losing discovery, routing, or security guarantees.
