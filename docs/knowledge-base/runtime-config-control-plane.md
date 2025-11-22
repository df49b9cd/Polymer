# Runtime Config & Control Plane

This page captures the proposed runtime-configuration path for OmniRelay (transport/data plane) working alongside MeshKit (discovery plane) and a dedicated control plane. Goals: hot changes (shadowing, cluster/region edits, limits), safe blast radius, native AOT friendliness, and transport-runtime stability.

## Plane Responsibilities
- **Control Plane**: owns desired state documents, schema/graph validation, rollout policy, audit, and capability gating; exposes REST/gRPC for CLI and automation; publishes versioned snapshots and change events.
- **MeshKit (Discovery Plane)**: gossips membership and emits stable identities/endpoints plus health; it never carries policy.
- **OmniRelay (Transport Plane)**: executes routing/shadowing using immutable snapshots; subscribes to discovery + config feeds; exposes no mutating APIs.

## Data Contracts
- **Config Snapshot**: versioned + signed JSON using source-generated `System.Text.Json` contexts; blocks: `metadata` (version, ttl, author), `spec` (dispatcher/shadowing/routing), `rollout` (target scopes, waves). Unknown fields rejected unless feature-gated.
- **Discovery Feed**: `EndpointsSnapshot` bootstrap + deltas with generation; scoped by service/cluster/region.
- **Capability Matrix**: each node reports supported `apiVersion`/features; control plane blocks forward-incompatible publishes.

## Distribution & Apply Flow
1. CLI/automation calls control plane with admin token → validate/dry-run → CAS write to backing store (Postgres/etcd/Consul) → audit entry → publish "version hash" event to bus (Kafka/Redis Stream/Service Bus).
2. OmniRelay watcher receives notify (push) or long-polls/pulls via ETag (fallback) → fetches snapshot → verifies signature/hash and capability → atomically swaps into `ConfigHost`.
3. `ConfigHost` invalidates generation-tagged caches and invokes `IConfigListener` hooks so dispatcher/router/shadowing rebuild idempotently without per-request locks.
4. Node readiness blocks until first valid snapshot; periodic reconciliation triggers if version staleness exceeds threshold.

## Rollout & Safety Rails
- Wave-based rollout: canary percentage → region/cluster → global; auto-rollback on SLO breach or divergence.
- Guardrails: destructive ops (region/cluster removal) require confirmation and optional shadow-first path; max change-size limits; schema + graph validation (no dangling clusters/cycles/bad weights).
- Drift detection: control plane compares desired vs reported `configVersion` and re-publishes or rolls back when inconsistent.

## Deployment Topology
- Control plane and bus run per cell/region for HA; backing store uses CAS and AZ replication.
- MeshKit gossip remains cell-local with optional cross-cell relays; OmniRelay nodes carry a failover list of control-plane endpoints.
- Pull fallback enables recovery when bus events are missed or during partition.

## MeshKit & Discovery Integration
- MeshKit stays the source of truth for service/member endpoints and health. OmniRelay nodes subscribe directly to MeshKit feeds (snapshot + deltas) and reconcile into in-memory endpoint sets tagged with a discovery generation.
- The control plane does **not** proxy discovery traffic. It optionally queries MeshKit for rollout guardrails ("is ≥X% of cluster healthy?") and for targeting resolution (e.g., list clusters in region `us-east`).
- Config snapshots carry routing intent (shadowing, weights, failover) but never embed concrete IP lists; OmniRelay joins the intent with live endpoints from MeshKit at apply time.
- Drift visibility: OmniRelay reports both `configVersion` and `discoveryGeneration` to the control plane, enabling detection of mismatched intent vs membership.

## Multi-Cell / Bridge Topology
- Each **cell** runs its own MeshKit gossip ring and local control-plane/bus pair; OmniRelay nodes prefer the in-cell control plane and fall back to a short list of neighbouring cells.
- **Bridge relays**: designate a small, throttled set of MeshKit relay nodes per cell pair (or per region) that forward membership digests across cells. These relays are the only nodes allowed to export/import inter-cell gossip.
- Use **allow/deny lists by service** on bridges to avoid global fan-out; only cross-cell services (e.g., auth, billing) are exported. Non-exported services remain cell-local for blast-radius isolation.
- Control-plane config includes a **topology map** (cells, regions, bridge pairs, exportable services, quotas). Snapshots carry this intent; MeshKit enforces it when deciding what to gossip across bridges.
- Health-based routing: rollout gates may require both `configVersion` alignment and `discoveryGeneration` coherence across the targeted cells before promoting traffic.
- Failure modes: if bridges flap, cells continue intra-cell routing; control plane can pause cross-cell rollout waves until bridges recover. Bridge relays emit metrics for replication lag and dropped updates.

### Exportable Service Criteria
- **Shard semantics**: export services whose state is global or replicated (identity, policy, catalog). Keep shard-local stateful workloads unexported unless they use multi-region consensus or read-only replicas.
- **Blast radius**: prefer idempotent/read-mostly APIs; destructive writes stay local or require stricter controls (two-phase commit, fencing, quotas).
- **Latency budget**: export only if added RTT meets SLO; otherwise keep local and use caching/replicas for cross-cell reads.
- **Data classification**: PII/financial writes default to non-exportable unless cross-cell storage, compliance, and audit are explicitly in place.
- **Dependency direction**: lower-layer platforms (authz, directory, policy, routing metadata) are strong candidates; product surfaces default to local unless mandated.
- **Capacity/quotas**: define cross-cell budgets; bridges enforce rate limits to prevent surge during failover.
- **Operational readiness**: service must own runbooks for cross-cell failover/rollback and clearly documented ownership; otherwise keep cell-local.

## Interop with Istio / Envoy
We can coexist with or layer on top of Istio/Envoy without putting the control plane on the data path.

- **North-south ingress/egress**: Use Envoy/Istio ingress gateways for TLS termination, WAF, and global traffic steering; hand off to OmniRelay for intra-app RPC. OmniRelay nodes trust the mesh mTLS identity (SPIFFE) and apply dispatcher rules from our snapshots.
- **Sidecar coexistence**: If workloads already run Istio sidecars, keep them for mTLS/policy and let OmniRelay bind to localhost endpoints; avoid double routing by disabling overlapping HTTP routing features in Envoy for OmniRelay ports.
- **Discovery bridge**: Optional translator in the control plane can expose MeshKit membership as xDS EDS to Envoy gateways so cross-mesh traffic can target OmniRelay-backed services without custom LDS/RDS in Envoy.
- **AuthN/AuthZ**: Continue to use Istio/Envoy ext_authz or JWT filters for ingress; embed the same JWKS/OPA endpoints into OmniRelay snapshots so enforcement is consistent inside the app. Keys/tokens still rotate via the control plane.
- **mTLS & PKI**: Either rely on Istio SDS-issued certs and mount them for OmniRelay, or have the control plane publish certs; choose one path per deployment to avoid dueling identities.
- **Telemetry**: Align on OTLP; Envoy exports to the same collector as OmniRelay. Label metrics/logs with `configVersion` and `meshVersion` to debug cross-layer rollouts.
- **Gradual adoption**: Start with Envoy as the boundary/gateway and run OmniRelay as the app’s transport plane; only add the xDS bridge if/when external meshes need dynamic service membership from MeshKit.

## Security & External Integrations
- Control plane handles operator authn/authz (OIDC/AAD/Auth0/SAML), issues scoped tokens, and signs snapshots; OmniRelay only verifies signatures/tokens locally and never exposes mutating APIs.
- Identity, secrets, and PKI integrations (Vault, AWS PCA, JWKS) live in the control plane; it publishes rotated artefacts (CA chains, leaf certs, JWKS) alongside config so the transport plane consumes immutable, cached material.
- External authz/OPA or ext_authz endpoints are configured in control-plane policy; OmniRelay simply enforces per-request using the derived endpoints/keys without embedding third-party SDKs.
- Traffic never transits the control plane; it is a routing/policy authority, not a proxy. Data-plane changes are applied via signed snapshots, not RPC calls on the hot path.
- Rotation policy (keys/certs/tokens) is centralized; transport nodes observe new versions through the same config/watch channel and fail closed on expired artefacts.

## Security & Observability
- mTLS between all planes; scoped tokens for read vs mutate; snapshots are signed.
- Metrics/events: fetch/apply latency, version, rollout progress, rollback count, discovery generation; logs capture diff summary, not full docs.
- Audit log for every mutation and read; CLI exposes `audit tail` and `status`.

## Operator Workflow (CLI)
- `omnirctl config get/set/validate --scope ... --dry-run` for edits.
- `omnirctl rollout start/status/rollback --version v123 --wave ...` for controlled rollout.
- `omnirctl shadow enable/disable --service <svc> --percent <p>` for traffic shadowing switches.
- `omnirctl audit tail --since 15m` for traceability.

## Testing Expectations
- Unit: scope precedence, validator failures, shadowing math, capability gating.
- Property: random graph generation to stress validator/merging logic.
- Integration: in-memory control plane + N nodes; confirm rollout waves, rollback on SLO breach, drift recovery.
- Soak: long-running shadowing toggles to catch leaks, lock contention, GC churn.

## Implementation Pointers (repo mapping)
- `src/OmniRelay` (transport): host `ConfigHost`, `IConfigListener`, readiness/health surfacing config version.
- `src/OmniRelay.Config`: shared DTOs + source-gen JSON contexts; lightweight validators used by both planes.
- `src/OmniRelay.ControlPlane` (new): API, CAS store adapter, rollout engine, audit sink, event publisher.
- `src/OmniRelay.Cli`: operator commands target control plane only (no data-plane mutation path).
- `docs/knowledge-base/` (this page) + samples/templates to guide operators.
