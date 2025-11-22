# OmniRelay Business Requirements Document

## 1. Introduction
### 1.1 Purpose
Define the business objectives, scope, and success criteria for OmniRelay as the high-performance, Native AOT-friendly platform. OmniRelay is delivered as three layers inside one repo and split into separate runtimes/assemblies: shared (Codecs/Protos/Transport.Host), `OmniRelay.DataPlane` (dispatcher, peers, extensions, filter chain, sidecar/edge/in-proc hosts), and `OmniRelay.ControlPlane` (MeshKit-facing services, registry, identity, rollout, bridge).

### 1.2 Scope
- In scope: L4/L7 proxying (DataPlane), routing, retries/circuit breaking, mTLS/JWT authn/authz enforcement, telemetry emission, dynamic extension hosting (DSL, Proxy-Wasm, native plugins), watchdog/resource guards, capability negotiation; ControlPlane services (MeshKit) for config/identity/registry/rollout; shared libraries for codecs/protos/host wiring.
- Out of scope: application business logic, UI consoles, non-.NET application SDKs (beyond agreed control-plane protocol clients).

### 1.3 Background
- OmniRelay replaces or complements classic sidecar proxies by offering a Native AOT-optimized data plane that can also embed in services to avoid an extra network hop.
- Extensions are delivered as signed packages (DSL/Wasm/native) to enable runtime customization without redeploying the host binary.

### 1.4 References
- docs/knowledge-base/dotnet-performance-guidelines.md
- docs/architecture/omnirelay-rpc-mesh.md
- docs/architecture/transport-layer-vision.md

### 1.5 Assumptions and Constraints
- Built with .NET Native AOT; no runtime IL generation or reflection in hot paths.
- Must sustain per-worker isolation and watchdog protections for extensions.
- Config and artifacts are signed; integrity checks run before load.
- Must operate with and without MeshKit present (static config allowed) but optimizes for MeshKit-driven control.

### 1.6 Document Overview
This BRD captures business goals, stakeholders, functional expectations, and operational requirements that guide the OmniRelay SRS and implementation.

## 2. Methodology
- Requirements derived from mesh patterns (sidecar and in-proc), extensibility needs (DSL/Wasm/native), and security/compliance expectations for zero-trust networking.
- Emphasizes performance (AOT), safety (sandbox/watchdogs), and rollout agility (hot-loadable extensions and config).

## 3. Functional Requirements
### 3.1 Context
- OmniRelay sits on the data path: `client -> OmniRelay -> service` (in-proc or sidecar) or `client -> OmniRelay (edge) -> backend`.
- MeshKit (control plane) computes and signs config; OmniRelay consumes deltas/snapshots.

### 3.2 User Requirements
- Platform/SRE: consistent policy enforcement, canary/rollback for filters/extensions, strong telemetry.
- Security: mTLS everywhere, signed artifacts, fail-open/closed controls, watchdog isolation.
- Service teams: minimal latency overhead, option to embed instead of sidecar, straightforward extension hooks.

### 3.3 Data Flow Diagrams (textual)
- Config path: MeshKit (central) -> MeshKit agent (optional) -> OmniRelay watch stream -> apply to worker-local VMs/filters.
- Request path (HTTP): client -> listener -> filter chain (Wasm/DSL/native) -> router -> upstream -> response returns via reverse chain.
- Telemetry path: OmniRelay -> local agent (optional) -> telemetry backend (OTLP).

### 3.4 Logical Data Model / Data Dictionary
- Route: host/path match rules + cluster target + per-route policy (retries, timeouts, CB).
- Cluster: upstream endpoints, LB policy, health checks, TLS settings.
- Extension package: manifest (name, version, ABI/capabilities, hash), payload (DSL/Wasm/native), signature.
- Capability set: flags advertised by OmniRelay build/host (runtimes available, limits, features).

## 4. Other Requirements
### 4.1 Interface Requirements
- Control-plane API: versioned protobuf/xDS-like watch; supports deltas and LKG snapshots.
- Extension host APIs: Proxy-Wasm ABI; native ABI via function pointers; DSL interpreter interface.

### 4.2 Data Conversion Requirements
- Config and artifacts are validated, hashed, and signed prior to load; backward-compatible schema evolution with explicit capability negotiation.

### 4.3 Hardware/Software Requirements
- Runs on x86_64 and ARM64; per-RID AOT builds; supports Linux and macOS for dev; production target Linux.
- Requires .NET 10 Native AOT runtime; optional Wasm runtime (V8 default; Wasmtime/WAMR if built).

### 4.4 Operational Requirements
- Hot reload for config/extensions; watchdogs for stuck extensions; fail-open/closed/reload policies.
- LKG cache for config to survive control-plane partitions.
- Metrics/logs/traces emission with rate limits per tenant.
- Canary/rollback controls for filters/extensions and routes.
