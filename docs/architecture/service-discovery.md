# Service Discovery + Peering Gaps

OmniRelay should own the connectivity fabric so product services stay focused on business logic (workflows, catalog updates, etc.). The features below describe what the mesh layer must deliver regardless of which domain-specific service rides on top.

This architecture note lives alongside `docs/architecture/omnirelay-rpc-mesh.md` and expands on the discovery and peering requirements outlined there.

Each subsection outlines the feature, why the mesh needs it, and objective success criteria. The following cross-cutting sections translate those needs into requirements, use-cases, proposals, and a staged plan.

## Goals

1. Deliver a mesh-native discovery plane so OmniRelay nodes can scale, fail over, and route requests without manual wiring.
2. Keep business services unaware of transport topology by exposing stable APIs/SDKs and automated tooling.
3. Provide auditable, policy-driven controls (security, leadership, observability) so operators can manage the mesh as shared infrastructure.

## Non-goals

- Define new business-domain RPCs beyond what existing services already ship.
- Replace external service meshes (Istio/Linkerd); this document scopes the OmniRelay-specific discovery plane only.
- Solve host provisioning or deployment orchestration—assume compute comes from the target environment (Kubernetes, VM scale sets, etc.).

## Assumptions

- All OmniRelay nodes can run side-by-side with the gossip agent and have consistent clock skew (<250 ms) for lease expiry.
- Persistent storage for registry metadata (e.g., etcd, Postgres, Redis) is available with HA guarantees matching the non-functional requirements.
- Existing diagnostics endpoints and CLI tooling can be expanded; we do not need a brand-new UX stack.

## Functional requirements

1. Automatic peer discovery, health dissemination, and leadership assignment without manual configuration in application services.
2. Deterministic routing metadata (shards, queues, clusters) exposed through stable OmniRelay APIs/SDKs.
3. Self-healing behavior: failed or degraded nodes trigger rebalancing and leadership changes with minimal operator input.
4. Secure enrollment ensuring only authenticated/authorized peers participate in gossip or receive routing data.
5. Cross-cluster awareness with controllable failover priorities and replication flows.
6. Operator accessibility via registry APIs, CLI, dashboards, and alerting that are consistent across teams.
7. Configurable but governed transport/encoding profiles so services can expose multiple endpoints without fragmenting the mesh; mesh-internal traffic defaults to gRPC over HTTP/3 with automatic downgrade to HTTP/2.
8. Continuous validation through automated chaos/testing harnesses prior to release.

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
- Implement **transport/encoding policy enforcement** so gRPC/Protobuf remains the mesh default while edge profiles (HTTP/1.1, HTTP/3, JSON, raw) are opt-in and validated.
- Implement **secure bootstrap** via mutual TLS with pluggable identity providers (SPIFFE, cloud-managed identities).
- Stand up a **chaos/test harness** to simulate churn, partitions, and upgrades.

- **Transport-aware observability** so operators can see where HTTP/3 has been negotiated vs. downgraded to HTTP/2.

## Transport & encoding strategy

- **Defaults**: All mesh-internal control, replication, and peer RPCs run on gRPC over HTTP/3 with Protobuf payloads. Negotiation during TLS/ALPN automatically downgrades to HTTP/2 when either side lacks HTTP/3 support, so legacy clients remain compatible. Leadership election, routing metadata, and replication streams must expose at least this profile.
- **Edge adapters**: Diagnostics, CLI, and browser-facing endpoints continue to expose HTTP/1.1 or HTTP/2 with JSON encoding. Optional HTTP/3 listeners can be enabled per endpoint for mobile/edge workloads; raw/opaque payload passthrough is reserved for scenarios that already encrypt or structure the data externally.
- **Configurability**: Dispatcher/host config may declare transport + encoding per namespace/endpoint, but selections must pass policy validation (e.g., raw encoding requires explicit ACLs, HTTP/1.1 disabled for intra-mesh traffic). Config is versioned so rollbacks remain deterministic.
- **Observability & negotiation**: Every request records chosen transport/encoding via existing OmniRelay headers (`rpc-transport`, `rpc-encoding`); Prometheus metrics and logs expose aggregates so we can track legacy usage ahead of deprecation.
- **Success criteria**:
  - 100% of internal control-plane traffic negotiates gRPC/HTTP/3 when possible and otherwise downgrades to HTTP/2, verified via telemetry.
  - Services can expose at least one alternate profile (HTTP/1.1+JSON or HTTP/3+JSON) by configuration only—no code change.
  - Config validation and policy checks reject unsupported combinations with actionable errors, and alerts fire if raw payload endpoints exceed configured budgets.

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
- **Configuration & policy**
  - Extend dispatcher/config builders so transport + encoding profiles are declared per endpoint, validated against policy (e.g., mesh-internal must be gRPC/Protobuf, raw payloads require ACL).
  - Surface policy state via `/control/config` and integrate with CI validation so misconfigured profiles fail before deployment.
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

## Risks & mitigations

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Split-brain leadership due to network partitions | Conflicting shard assignments, duplicate processing | Use fencing tokens + monotonic versioning; require quorum acknowledgements before publishing routing tables; add alerting on concurrent leaders. |
| Gossip storm / excessive traffic at scale | Elevated CPU/network, delayed convergence | Enforce fanout/backoff limits, introduce tiered gossip beyond threshold, expose SLO metrics for RTT + message rate. |
| Certificate/identity misconfiguration | Nodes fail to join or unauthorized peers infiltrate mesh | Provide automated bootstrap tooling, enforce revocation lists, and document roll-forward/rollback for identity providers. |
| Operator overload from new control APIs | Slow incident response | Ship curated dashboards, CLI defaults, and runbooks before GA; run tabletop exercises to vet workflows. |
| Upgrade incompatibilities | Service disruption during rolling updates | Define wire-format versions, build mixed-version CI tests, add feature flags to gate new capabilities until fleet upgraded. |

## Open questions

1. Which backing store (etcd, Postgres, Redis, Azure Cosmos DB) best satisfies the registry durability/latency requirements while fitting existing hosting environments?
2. Do we integrate with external service meshes (Istio/Linkerd) for mTLS or own the full TLS stack inside OmniRelay?
3. How should we expose discovery data to non-.NET consumers (e.g., REST/GraphQL gateway), and do we need generated SDKs?
4. What is the target SLO for cross-cluster replication lag, and does it vary by workload class?
5. Should we support pluggable leader-election providers so customers can bring their own (e.g., Kubernetes Lease, ZooKeeper)?

## References

- `docs/architecture/omnirelay-rpc-mesh.md` – broader mesh architecture context.
- `docs/reference/distributed-task-leasing.md` – details on lease dispatchers, health trackers, and replication components referenced in this plan.
- `docs/reference/diagnostics.md` – existing control-plane endpoints that will evolve into the new registry APIs.
- `samples/ResourceLease.MeshDemo/README.md` – current sample showing how mesh roles map to real workloads; will be updated to consume the new discovery plane.

## 1. Membership gossip layer + leader elections

- **Feature**: Embed a Ringpop/memberlist-style gossip transport so every OmniRelay node (edge gateways, schedulers, workers, domain services) automatically exchanges membership, health, and metadata. On top of that, run lightweight elections (Raft/lease or SWIM+fencing) to select a global control leader and shard-specific leaders inside the mesh plane.
- **Reasoning**: Static URLs make scaling brittle and leak infrastructure concerns into business services. Gossip lets the mesh self-heal and converge quickly, keeping discovery transparent to applications. Leader leases avoid split-brain when changing shard ownership, dialing backpressure knobs, or orchestrating failover across any hosted service.
- **Capabilities**:
  - Gossip agent handles member join/leave, health scores, and metadata broadcasting with backoff controls.
  - Leader-election layer issues fenced leases (global + shard) stored in the registry.
  - Membership metadata encodes role, cluster, locality, mesh-version, and security posture for downstream policy engines.
- **Interfaces & data contracts**:
  - `membership.proto/json` surfaces the gossip payload schema (node id, role, status, version, ttl).
  - Leader election events published via `/control/events/leadership` SSE/gRPC-stream for observability and automation.
- **Operational considerations**:
  - Gossip fanout, suspicion timers, and retransmit limits must be configurable per environment.
  - Leadership changes emit structured logs with correlation IDs; on-call runbook links back to this section.
- **Success criteria**:
  - Node joins propagate to 95% of peers < 2s; departures trigger eviction and health updates.
  - Leader transitions are observable (Prometheus counter + log) and take < 5s; only one leader issues shard assignments at a time (verified via fencing token).
  - Removing any routing-capable node triggers automatic leader failover and other nodes resume work without manual restarts.

### Implementation snapshot (v1)

- `MeshGossipHost` (`src/OmniRelay/Core/Gossip/`) now runs inside every OmniRelay host via `AddMeshGossipAgent`. It uses HTTP/3 + mTLS, reloads the same bootstrap certificates as the transports, and auto-starts/stops with the dispatcher lifecycle. Fanout, suspicion timers, retransmit budgets, metadata refresh, and port/adverts are all configurable through `mesh:gossip:*` settings (see table below). Configuration can be supplied via `appsettings` or environment variables such as `MESH_GOSSIP__ROLE=gateway`.
- Prometheus/OpenTelemetry instrumentation exposes `mesh_gossip_members`, `mesh_gossip_rtt_ms`, and `mesh_gossip_messages_total` meters. `AddOmniRelayDispatcher` registers the `OmniRelay.Core.Gossip` meter so dashboards can chart membership counts, RTT, and failure rates next to RPC metrics.
- Structured logs now describe gossip events once per transition: peer joins, recovers, becomes suspect, or leaves. Logs include `peerId`, `role`, `clusterId`, `region`, `meshVersion`, and whether HTTP/3 was negotiated.
- `/control/peers` (and its legacy alias `/omnirelay/control/peers`) is available on every HTTP inbound whenever gossip is enabled. It returns the gossip snapshot (status, lastSeen, RTT, metadata) even before the persistent registry ships, fulfilling the diagnostics requirement from DISC-001.
- Peer health diagnostics reuse gossip metadata by pushing labels (`mesh.role`, `mesh.cluster`, etc.) into `PeerLeaseHealthTracker`, so `/omnirelay/control/lease-health` stays consistent with `/control/peers`.
- Sample configurations:
  - `samples/ResourceLease.MeshDemo/appsettings.Development.json` – single-node dev defaults that enable gossip with a self-signed cert and loopback seeds.
  - `samples/ResourceLease.MeshDemo/appsettings.Production.json` – production-friendly template showing separate ports, TLS paths, certificate pinning, and fanout overrides.

#### Configuration knobs

| Setting | Description | Default |
| --- | --- | --- |
| `mesh:gossip:enabled` | Turns the gossip agent on/off. | `true` |
| `mesh:gossip:nodeId` / `role` / `clusterId` / `region` / `meshVersion` | Metadata embedded in every envelope for policy/diagnostics. | Machine name + `worker`/`local`/`dev`. |
| `mesh:gossip:port` / `bindAddress` / `advertiseHost` / `advertisePort` | Listener + advertised endpoint. | `0.0.0.0:17421`, advertises host name. |
| `mesh:gossip:fanout` | Number of peers to ping each interval. | `3` |
| `mesh:gossip:interval` | Interval between gossip rounds. | `00:00:01` |
| `mesh:gossip:suspicionInterval` | Grace period before a peer becomes suspect. | `00:00:05` |
| `mesh:gossip:pingTimeout` | Timeout for outbound gossip HTTP calls. | `00:00:02` |
| `mesh:gossip:retransmitLimit` | Retries before marking a peer dead. | `3` |
| `mesh:gossip:metadataRefreshPeriod` | How often the agent bumps its metadata version. | `00:00:30` |
| `mesh:gossip:seedPeers` | Static bootstrap peers (`host:port`). | Empty (optional). |
| `mesh:gossip:tls:*` (`certificatePath`, `certificatePassword`, `checkCertificateRevocation`, `allowedThumbprints`) | mTLS settings shared with the dispatcher. Certificates are reloaded automatically if rotated on disk. | Path/env specific. |

Env overrides follow standard ASP.NET Core conventions (for example `MESH_GOSSIP__TLS__CERTIFICATEPATH=/mnt/certs/gossip.pfx`).

#### Metrics + alerts

| Metric | Purpose | Alert hint |
| --- | --- | --- |
| `mesh_gossip_members{mesh.status=alive|suspect|left}` | Member counts per status. | Alert if `mesh.status="suspect"` climbs steadily or `alive` drops suddenly. |
| `mesh_gossip_rtt_ms` | Histogram for gossip round-trip time per peer. | Alert if P95 > gossip interval × 2. |
| `mesh_gossip_messages_total{mesh.direction,mesh.outcome}` | Outbound/inbound success/failure counters. | Alert on sustained `mesh.outcome="failure"` growth. |

Grafana/Prometheus rules from DISC-001 now have concrete signals to target, and the sample dashboards already include the new meters.

### Leadership service (v1)

- `LeadershipCoordinator` + `LeadershipEventHub` (under `src/OmniRelay/Core/Leadership/`) run alongside every dispatcher once `mesh:leadership` is configured. Coordinators pull gossip metadata, acquire fenced leases through the shared `ILeadershipStore`, and publish transitions into the hub so HTTP + gRPC endpoints can stay in sync.
- `/control/leaders` returns JSON snapshots (optionally filtered by `?scope=`) while `/control/events/leadership` pushes SSE updates that log the negotiated HTTP protocol and include `leaderId`, `term`, `fenceToken`, and expiry timestamps. The gRPC inbound now exposes `LeadershipControlService.Subscribe` (HTTP/3 default, HTTP/2 downgrade) defined in `Core/Leadership/Protos/leadership_control.proto` for the same event stream over Protobuf.
- Metrics `mesh_leadership_transitions_total`, `mesh_leadership_election_duration_ms`, and `mesh_leadership_split_brain_total` measure churn, convergence time, and split-brain detections. Coordinators also emit structured warnings when incumbents disappear from gossip so runbooks can link straight to the offending scope.
- Operators can query `omnirelay mesh leaders status` (new CLI subcommand) to print the current tokens or `--watch` the SSE feed locally. ResourceLease demo configs were updated with sample `scopes` + `shards` so the leadership service starts automatically in dev/prod templates, satisfying the quick-start requirement in this section.

## 2. Shard ownership + routing metadata

- **Feature**: Maintain consistent-hash or rendezvous maps that describe which mesh node owns each shard/task queue, version them, and expose via RPC/HTTP (`/control/shards`) plus watch streams.
- **Reasoning**: Client SDKs, peer choosers, and observability layers need deterministic routing regardless of the business workload. Without ownership metadata, the mesh would thrash or double-deliver messages, forcing application teams to handle routing themselves.
- **Capabilities**:
  - Deterministic hashing strategies (ring, rendezvous, locality aware) selectable per namespace.
  - Versioned shard tables persisted in the registry with diff history for auditing.
  - Watch semantics that notify SDKs/agents when shards move or are paused.
- **Interfaces & data contracts**:
  - `/control/shards` (REST + gRPC) returns shard ownership, leadership token, capacity hints, and version checksum.
  - Client SDK caching module persists the current map and automatically retries on version mismatch.
- **Operational considerations**:
  - Rebalance operations must support dry-run + approval workflows for regulated environments.
  - Provide tooling to simulate shard impact (percentage movement) before applying a new hash ring.
- **Success criteria**:
  - RPC exposes shard table with version + checksum; peer choosers can fetch and cache it.
  - During reshards the delta is < 10% of shards unless forced (keeps hot queues stable).
  - CLI tooling can list which mesh node owns shard `N` and the owning leader; synthetic tests show zero duplicate deliveries after a reshard event.

## 3. Health-aware rebalancing

- **Feature**: Periodic controller loop that ingests gossip health, workload metrics, and queue depth to decide when to pause shards or reassign them. Publish state transitions (`steady`, `draining`, `rebalancing`).
- **Reasoning**: Application teams shouldn’t babysit node failures. The mesh must automatically move shards off unhealthy hosts so top-layer services continue processing without manual routing tweaks, mirroring managed platforms like Cadence.
- **Capabilities**:
  - Controller consumes metrics (pending depth, latency, error rate) plus gossip health to rank nodes.
  - Can drain, throttle, or reassign shards; supports staged rollout (evaluate → drain → move).
  - Emits state machine events for `steady`, `investigating`, `draining`, `rebalancing`, `completed`.
- **Interfaces & data contracts**:
  - `/control/rebalance-plans` endpoint to preview and approve moves; events broadcast over SSE/gRPC for observers.
  - Config CRD/JSON schema defining thresholds per namespace/cluster.
- **Operational considerations**:
  - Provide guardrails (max parallel moves) to avoid cascading failures.
  - Integrate with on-call tooling: controller raises alerts when manual approval is waiting or when automated action fails.
- **Success criteria**:
  - When a processing node misses heartbeats for N seconds, the loop parks its shards and reassigns within 2 lease intervals.
  - Rebalance events raise structured metrics/logs so Grafana can show “Shard 12 draining from node-a to node-b”.
  - End-to-end request/lease latency stays within ±20% during planned failovers on synthetic load.

## 4. Discoverable peer registry API

- **Feature**: Control-plane API (`/control/peers`, `/control/clusters`) returning JSON/stream updates about peers, roles, health, leadership, and advertised endpoints. Include filters, pagination, and watch semantics.
- **Reasoning**: Domain teams and automation need a service-agnostic inventory to interrogate the mesh. A registry decouples product code from infrastructure endpoints and enables tooling/portals to show live topology.
- **Capabilities**:
  - Read APIs with server-side filtering (role, cluster, version, health) and pagination for large fleets.
  - Mutation APIs for administrative actions (cordon, drain, label updates) with audit trails.
  - Streaming interface (WebSocket/SSE/gRPC) for near-real-time updates powering dashboards.
- **Interfaces & data contracts**:
  - Versioned schema (`PeerRecord`, `ClusterRecord`) maintained in `diagnostics.openapi.json`.
  - RBAC scopes (`mesh.read`, `mesh.operate`, `mesh.admin`) enforced via existing auth providers.
- **Operational considerations**:
  - Include rate limits + caching guidance so dashboards don’t overload the control plane.
  - Provide sample queries/CLI usage in runbooks for common incident workflows.
- **Success criteria**:
  - API returns consistent snapshots < 200ms and supports ETag/version to detect drift.
  - CLI command `omnirelay mesh peers list` can rely solely on the API.
  - Registry enforces RBAC/mTLS so only authorized callers can read/write peer metadata.

## 5. Multi-cluster awareness

- **Feature**: Define cluster IDs, replication streams, and failover priorities. Gossip must propagate cluster info; leaders advertise cross-cluster endpoints and version vectors (similar to Cadence global domains).
- **Reasoning**: Services expect the mesh to provide geo-redundancy. Without cluster metadata, they would have to hardcode regions or run their own replication. Keeping this logic in the mesh keeps domain code portable.
- **Capabilities**:
  - Registry stores cluster descriptors (id, region, priority, failover state, replication endpoints).
  - Dispatchers replicate event logs across clusters with configurable retention/lag SLOs.
  - Failover orchestration updates routing metadata and client endpoints atomically.
- **Interfaces & data contracts**:
  - `/control/clusters` API returns cluster state, replication lag, and governance metadata (owners, change tickets).
  - Replication streams expose `ClusterVersionVector` headers so downstream services detect staleness.
- **Operational considerations**:
  - Provide runbooks and automation for planned failover vs. emergency failover; integrate with change-management tooling.
  - Support partial activation (subset of namespaces) to limit blast radius during drills.
- **Success criteria**:
  - Clusters declare `state=active|passive|draining` and publish monotonically increasing failover versions.
  - Mesh replication services stream logs to followers; lag metrics stay < configurable threshold.
  - Killing the active cluster while promoting the passive one results in < 30s downtime for externally visible mesh APIs.

## 6. Secure peer bootstrap

- **Feature**: TLS mutual auth + ACLs for gossip and control-plane endpoints. Provide bootstrap tokens or signed certificates so only authorized nodes participate. Optionally integrate with SPIFFE/SPIRE or cloud identity.
- **Reasoning**: Infrastructure security should be uniform regardless of which service uses OmniRelay. Hardening the mesh frees application code from embedding bespoke ACL logic.
- **Capabilities**:
  - PKI automation issues short-lived workload certs with embedded mesh roles and cluster ids.
  - Bootstrap workflow validates environment posture (image signature, config) before granting mesh credentials.
  - Gossip/control channels enforce TLS 1.3, preferred cipher suites, and certificate pinning.
- **Interfaces & data contracts**:
  - `omnirelay cert issue` CLI command and API for automated enrollment; returns cert bundle + metadata.
  - Policy CRDs describing which roles or namespaces may join specific clusters.
- **Operational considerations**:
  - Rotate certs automatically with overlap windows; alert if rotation falls behind.
  - Provide emergency revocation procedure and documented recovery for lost-root scenarios.
- **Success criteria**:
  - Peers without valid certs or tokens fail to join; audit logs capture the attempt.
  - Certificate rotation happens without dropping more than 1 gossip RTT.
  - Security posture documented (threat model + hardening guide) and validated via automated tests.

## 7. State convergence + conflict resolution

- **Feature**: Add vector clocks or monotonic versioning for shard maps, leadership, and peer metadata plus background reconciliation tasks that repair divergence after network partitions.
- **Reasoning**: Gossip can temporarily fork state; without conflict resolution stale leaders could keep serving traffic. Versioned metadata and reconciliation ensure deterministic, eventually consistent outcomes.
- **Capabilities**:
  - Every registry record carries `Version`, `LastWriterId`, and optional `VectorClock`.
  - Background reconciler scans for divergent records and reapplies authoritative state from quorum stores.
  - Tombstone/GC subsystem expires dead peers and historical shard assignments safely.
- **Interfaces & data contracts**:
  - `/control/state-diff` endpoint lists pending conflicts or divergent records for operators.
  - Audit log stream captures reconciliation actions with before/after snapshots.
- **Operational considerations**:
  - Provide configurable GC windows per environment (dev vs prod) and document storage sizing.
  - Alert when reconciliation exceeds thresholds or cannot reach quorum (signals partition).
- **Success criteria**:
  - After a simulated partition, all peers converge to the same shard map version within 2× gossip interval.
  - Conflicting leadership claims trigger automated fencing (losing leader steps down) and emit alerts.
  - Registry garbage-collects tombstoned peers automatically, keeping stale entries < 1%.

## 8. Scalability & tiered topology

- **Feature**: Document and enforce limits (max peers per cluster, shard count, gossip fanout). Support hierarchical gossip (regional supervisors) when exceeding thresholds.
- **Reasoning**: Mesh may grow beyond lab scale; knowing the safe envelope avoids pathological fanout or gossip storms.
- **Capabilities**:
  - Config-driven guardrails for peers/shards per cluster, message rate, and control-plane QPS.
  - Tiered topology mode introduces regional supervisors/aggregators that bridge gossip domains.
  - Autoscaling signals emitted when approaching capacity (e.g., 80% of shard slots used).
- **Interfaces & data contracts**:
  - `/control/capacity` API reports current usage vs. configured limits.
  - Supervisor nodes expose metrics for cross-region fanout, queue depth, and memory.
- **Operational considerations**:
  - Provide migration guide for moving from single-tier to tiered topology without downtime.
  - Document recommended hardware sizing and benchmarking methodology.
- **Success criteria**:
  - Load tests show stable convergence up to target node count (e.g., 200 peers/cluster) with CPU < 30%.
  - Beyond the threshold, operators can enable tiered gossip (region leaders) via config and see traffic drop proportionally.
  - Metrics expose gossip queue length/fanout so alerts fire before saturation.

## 9. Upgrade & compatibility strategy

- **Feature**: Define protocol versions for gossip messages, shard schemas, and APIs. Include feature flags/rollout bits so mixed-version clusters operate safely during upgrades.
- **Reasoning**: Without compatibility guarantees, rolling upgrades could disrupt discovery. Version negotiation keeps older nodes functional until the fleet is updated.
- **Capabilities**:
  - Wire-format descriptors advertise supported versions + optional capabilities per node.
  - Feature flag service (config + runtime API) gates new behaviors until enough nodes support them.
  - Upgrade controller validates cluster health + compatibility matrix before allowing rollouts.
- **Interfaces & data contracts**:
  - `/control/versions` reports supported wire/API versions per node plus fleet-wide compliance summaries.
  - Release pipeline consumes a machine-readable compatibility document (e.g., `versions.yaml`).
- **Operational considerations**:
  - Provide runbooks for rolling back partial upgrades and for forcing incompatible nodes off the mesh.
  - Capture upgrade metrics (duration, errors) for continuous improvement.
- **Success criteria**:
  - Mixed-version chaos tests (N-1) run green; unsupported combinations are rejected with clear logs.
  - Upgrade guide documents sequencing (control plane first, data plane second) and automated health gates.
  - Feature flags allow canarying new discovery features with <10% of nodes before global rollout.

## 10. Operator tooling & observability

- **Feature**: Ship CLI commands, dashboards, and alerting rules dedicated to discovery/peering (leader status, shard skew, gossip latency).
- **Reasoning**: APIs alone are insufficient; operators need curated UX to debug incidents quickly.
- **Capabilities**:
  - Opinionated Grafana dashboards, alert rules, and runbooks versioned with the codebase.
  - `omnirelay mesh` CLI plugin offering read + operational verbs (cordon, drain, rebalance, inspect).
  - Telemetry exporters integrate with OTLP/Prometheus for hybrid deployments.
- **Interfaces & data contracts**:
  - Dashboard JSON + alert YAML stored under `docs/observability/` with release automation.
  - CLI uses the same registry APIs; output contract documented for scripting/automation.
- **Operational considerations**:
  - Provide RBAC-aware views (SRE vs developer) and ensure alerts map to actionable runbooks.
  - Include synthetic checks that validate dashboards/alerts after upgrades.
- **Success criteria**:
  - Grafana dashboard visualizes leadership, shard distribution, gossip RTT, and error budgets.
  - `omnirelay mesh` CLI supports `peers list`, `leaders status`, `shards rebalance`, with auth via existing credentials.
  - Alert rules (Prometheus) cover leader flaps, shard imbalance, gossip failure rate > threshold.

## 11. Testing & chaos validation

- **Feature**: Create automated scenarios (integration suite + chaos harness) that kill leaders, partition networks, spike membership churn, and measure whether discovery meets SLAs.
- **Reasoning**: Success metrics need continuous verification; chaos drills prevent regressions.
- **Capabilities**:
  - Deterministic integration clusters (docker-compose/Kubernetes) executing scripted drills (kill leader, partition region, surge joins).
  - Metrics collectors capture convergence, failover duration, and error rates for every run.
  - Automated ticketing when chaos outcomes breach SLOs.
- **Interfaces & data contracts**:
  - `chaos-scenarios.yaml` declares experiments, triggers, and expected SLOs; used by CI + on-demand runs.
  - Test harness emits machine-readable reports (JUnit/JSON) consumed by CI dashboards.
- **Operational considerations**:
  - Schedule weekly chaos runs in staging + quarterly in production-like environments.
  - Publish playbooks summarizing findings and backlog items from each drill.
- **Success criteria**:
  - CI job runs nightly chaos suite and publishes convergence metrics; failures block release.
  - Synthetic load generator demonstrates steady throughput/backpressure while chaos is active.
  - Postmortem templates capture discovery metrics so learnings feed back into design.

## Recommended topology

- Combine the gossip mesh with consensus-backed leader leases. Gossip offers resilient membership and health dissemination; leadership (global + shard-level) keeps configuration, shard assignment, and cross-cluster routing consistent.
- Ensure leaders publish their view of shard ownership so peer choosers and clients route deterministically, and emit Prometheus/Grafana signals for leader elections to aid debugging.
- Keep the control plane logically centralized (one truth for policy) but physically distributed by running the leader elections over multiple nodes—this balances governance with failure tolerance. Success means operators can kill any single node (including the current leader) without losing discovery, routing, or security guarantees.

## Implementation backlog

1. **Gossip + leadership foundation**
   - Implement a reusable gossip host component (based on memberlist/Ringpop) with configurable fanout, suspicion timers, retransmit limits, and metadata payload schema (`nodeId`, `role`, `clusterId`, `version`, `http3Support`).
   - Integrate the gossip host into dispatcher, gateway, and background-service hosts; add health instrumentation (`mesh_gossip_members`, `mesh_gossip_rtt_ms`).
   - Build a Raft/lease-based leadership service that consumes gossip membership, emits fenced leader tokens (global + per shard), and exposes `/control/events/leadership` stream plus Prometheus counters.
   - Document operational knobs (ports, TLS, firewall rules) and provide sample configs for dev/prod clusters.
2. **Routing metadata service**
   - Define the shard record schema (owner node id, leader id, capacity hints, status, version) and store it in a durable registry (Postgres/etcd/Redis abstraction with optimistic concurrency).
   - Implement deterministic hash strategies (ring, rendezvous, locality-aware) with policy selection per namespace; include diff history for auditing rebalances.
   - Build `/control/shards` REST/gRPC endpoints with pagination, filtering, watch streams, and ETag/version headers; add CLI commands (`omnirelay mesh shards list`, `... shards diff`, `... shards simulate --strategy rendezvous`).
   - Update peer choosers/outbounds to subscribe to the shard watcher, cache maps, and react to version changes without restarting processes.
3. **Health-aware rebalancer**
   - Deliver a controller service that ingests metrics (pending depth, error rate, latency) + gossip health, evaluates policy thresholds, and emits state-machine events (`steady`, `investigating`, `draining`, `rebalancePending`, `completed`).
   - Provide dry-run and approval workflows (`/control/rebalance-plans`) so regulated environments can review moves; support guardrails (max parallel shard moves, cooldowns).
   - Integrate with Prometheus/Grafana and alerting to visualize active rebalances; ensure draining logic gracefully handles leases in-flight across transports.
4. **Peer/cluster registry APIs**
   - Build the versioned control-plane endpoints (`/control/peers`, `/control/clusters`, `/control/versions`, `/control/config`) with RBAC scopes, pagination, filtering, and streaming updates (SSE/gRPC).
   - Add mutation verbs: cordon/uncordon peer, drain peer, update labels, promote cluster, edit config; include audit logging (who/what/when) and ETag enforcement.
   - Wire dashboards and the `omnirelay mesh` CLI to these APIs; document example workflows (list unhealthy peers, cordon node, verify draining).
5. **Security + bootstrap tooling**
   - Integrate SPIFFE/SPIRE or cloud workload identity provider to mint short-lived certs embedding mesh role + cluster id; enforce mTLS on gossip/control channels with TLS 1.3.
   - Extend configuration to express join policies (allowed roles, clusters, required attestation) and surface them through `/control/config`.
   - Ship `omnirelay cert issue`, `omnirelay mesh join`, and `omnirelay mesh revoke` commands plus documentation/runbooks; ensure audit logs capture enrollment/revocation events.
6. **Multi-cluster + replication**
   - Introduce cluster descriptors (state, priority, replication endpoints) and store them in the registry; add `/control/clusters` APIs + CLI for management.
   - Enhance replication services to attach version vectors, stream logs cross-cluster, and expose lag metrics (`mesh_replication_lag_seconds`); build failover orchestration (planned + emergency) with scripted automation and guardrails.
   - Publish detailed runbooks covering activation, failback, and mixed-active scenarios; integrate with change-management tooling for approvals.
7. **Transport/encoding governance**
   - Extend dispatcher/host config schema to declare per-endpoint transport + encoding; implement validation rules (mesh-internal must include gRPC/HTTP3+Protobuf with HTTP/2 fallback, raw encodings require ACL).
   - Emit telemetry counters for negotiated transport/encoding combinations and add alerting if legacy/unsupported pairings appear.
   - Update samples/docs/docker assets to highlight the new defaults, configuration knobs, and observability outputs.
8. **Observability + operator tooling**
   - Build Grafana dashboards (leadership status, shard distribution, gossip RTT, transport negotiation, replication lag) and Prometheus alert rules (leader flap, gossip RTT high, shard imbalance).
   - Enhance the `omnirelay mesh` CLI with subcommands: `peers list/status/drain`, `leaders status`, `shards list/simulate/rebalance`, `clusters promote`, `config validate`.
   - Add synthetic checks ensuring control APIs respond, leadership tokens rotate, and HTTP/3 negotiation works; document RBAC-aware views/runbooks.
9. **Testing & chaos infrastructure**
   - Create multi-node integration environments (docker-compose + Kubernetes Helm chart) capable of running scripted chaos experiments (kill leader, partition cluster, introduce version skew).
   - Develop `chaos-scenarios.yaml` plus harness tooling that triggers experiments, records convergence metrics, and exports reports to CI dashboards.
   - Automate nightly chaos pipelines and pre-release validation gates; include postmortem templates, automated ticket filing when SLOs breach, and artifact retention for forensic analysis.
