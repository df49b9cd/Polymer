# MeshKit Software Requirements Specification

## 1. Purpose
### 1.1 Definitions
- MeshKit: control-plane services plus optional local agents and mesh bridges that manage OmniRelay instances.
- Control domain: scope within which leader election/gossip applies (region/cluster/tenant).
- LKG: last-known-good config cached at agents.
- Bridge: component federating selected state between control domains without merging consensus.

### 1.2 Background
MeshKit provides the configuration, identity, policy, and extension lifecycle needed to run OmniRelay globally with both centralized and edge-aware topologies, built atop the `OmniRelay.ControlPlane` runtime and shared packages (Codecs/Protos/Transport.Host) without embedding data-plane logic.

### 1.3 System Overview
Components: central control-plane ring (leader + followers) per domain; local agents per node/pod; mesh bridges connecting domains; extension registry; CA/identity service; telemetry collector; operator API/CLI.

### 1.4 References
- docs/architecture/meshkit-extraction-plan.md
- docs/architecture/service-discovery.md
- docs/architecture/omnirelay-rpc-mesh.md

## 2. Overall Description
### 2.1 Product Perspective
- **System Interfaces**: northbound API/CLI/webhook; southbound gRPC watch streams to OmniRelay/agents; artifact registry endpoints; CA/CSR endpoints; telemetry ingest (OTLP/HTTP).
- **User Interfaces**: CLI/REST/gRPC admin APIs; dashboards optional; operator alerts via existing observability stack.
- **Hardware Interfaces**: network only; no specialized hardware.
- **Software Interfaces**: pluggable storage (etcd/SQL/doc store), signing service (HSM or software keys), OCI/HTTP artifact store, TLS stack for CA.
- **Communication Interfaces**: gRPC for control streams, OTLP for telemetry, HTTPS for artifact fetch and admin APIs.
- **Memory Constraints**: sized for control-plane workloads; agents lightweight with bounded caches.

### 2.2 Design Constraints
- Operations: zero-downtime config publish; canary/rollback; explicit epochs/terms for reconciliation; no leader election in agents/bridges.
- Site Adaptation: per-region/tenant domains; bridges filter/transform exported state; capability-aware down-leveling for heterogeneous OmniRelay builds.

### 2.3 Product Functions
- Compute and validate routes, clusters, and policies; sign and publish deltas/snapshots.
- Manage identity (CSR intake, issuance, rotation, trust-bundle distribution).
- Maintain extension registry (DSL/Wasm/native) with admission checks, rollout stages, and failure policies.
- Serve watch streams to OmniRelay/agents; provide LKG snapshots and capability negotiation.
- Ingest telemetry/health, correlate with config versions, and surface rollout health.
- Federate between domains via bridges with export allowlists and identity mediation.

### 2.4 User Characteristics
- Operators/SREs managing multi-region services; security teams managing PKI/policies; platform engineers integrating with CI/CD and GitOps.

### 2.5 Constraints, Assumptions, Dependencies
- Depends on reliable store for state; artifacts signed with pinned keys; assumes network between central and agents; supports partial connectivity via LKG caches; assumes OmniRelay advertises capabilities.

## 3. Specific Requirements
### 3.1 External Interface Requirements
- Admin API/CLI: create/update policies, routes, extensions, identity settings; observe rollout status; trigger canary/rollback; kill switches.
- Southbound: gRPC/xDS-like watch streams with deltas and snapshots; capability negotiation headers/fields; streaming errors include retry/backoff hints.
- Artifact registry: serve signed bundles (DSL/Wasm/native) via OCI/HTTP; provide metadata (hashes, ABI, dependencies, rollout tags).
- CA endpoints: CSR submit, cert issuance, rotation schedule; trust bundle retrieval.
- Telemetry ingest: OTLP/HTTP or gRPC; per-tenant rate limits; correlation with config epoch.

### 3.2 Performance Requirements
- Config propagation latency within target SLO (set per region); control-plane should not be on request hot path; agents must push LKG within seconds after disconnect.
- Control-plane should scale to global fleets with sharding; bridges must not materially increase propagation delay beyond configured bounds.

### 3.3 Logical Database Requirement
- Persistent store for desired state, issued cert metadata, rollout history, audit logs; cached copies of artifacts with hashes.

### 3.4 Software System Attributes
- **Reliability**: leader election within domain; bridges queue/replay deltas during outages; agents fallback to LKG.
- **Availability**: horizontally scalable control plane; stateless frontends with shared store; agents survive central outages.
- **Security**: signed configs/artifacts; mTLS on all control channels; RBAC for operators; audit logging; per-domain trust boundaries; bridge-level export policies.
- **Maintainability**: versioned schemas; capability negotiation; feature flags; modular roles (central/agent/bridge) sharing codebase.
- **Portability**: deployable on Kubernetes/VMs; supports x86_64/ARM64; pluggable runtimes for stores and artifact backends.

### 3.5 Functional Requirements
#### Functional Partitioning
- Policy/route engine, identity/CA, extension registry, rollout manager, telemetry collector, domain consensus layer, agent, bridge.

#### Functional Description
- Validate and sign config bundles; publish via watches; maintain epochs/terms per domain.
- Accept CSR and issue/rotate certs; distribute trust bundles; revoke if needed.
- Store and serve extension artifacts; enforce admission checks; drive canary and rollout policies; expose kill switch per artifact.
- Collect telemetry/health; correlate with versions; trigger alerts on failure/rollback conditions.
- Bridge: subscribe to source domain, filter/transform exported state, republish to target domain with new epochs/capabilities.
- Agent: subscribe to domain, cache LKG, renew certs, forward telemetry, never participates in elections.

#### Control Description
- Consensus state machine per domain (leader/follower); publish loop with backpressure; rollback process triggered by SLO breach or operator command; epoch/term monotonicity enforced; bridge translation pipelines with allowlists.

### 3.6 Environment Characteristics
- **Hardware**: standard cloud/node instances.
- **Peripherals**: none beyond storage and network.
- **Users**: operators/SRE, security/PKI admins, platform engineers.

### 3.7 Other
- Testing: contract tests for southbound schema; chaos tests for partition/bridge failure; perf tests for propagation latency; security tests for signature/PKI.
- Deployment: domain-scoped control-plane rings; optional local agents per node/pod; optional mesh bridges between domains; GitOps/CI integration for change promotion.
