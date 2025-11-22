# OmniRelay Software Requirements Specification

## 1. Purpose
### 1.1 Definitions
- OmniRelay: Native AOT-optimized data-plane proxy/library with extensible filter pipeline.
- DSL: Domain-specific rule packages interpreted by OmniRelay.
- Proxy-Wasm: ABI for Wasm extensions; runtimes include V8/Wasmtime/WAMR.
- LKG: Last-known-good configuration snapshot.

### 1.2 Background
OmniRelay replaces or complements sidecar proxies by providing an in-proc option plus sidecar/headless modes, while retaining mesh-grade features (mTLS, routing, policy, extensibility) and AOT performance characteristics. The implementation is split into `OmniRelay.DataPlane` (hot path) and `OmniRelay.ControlPlane` (control/diagnostics hosting) over shared packages.

### 1.3 System Overview
Components: listener/pipeline, routing/cluster manager, extension hosts (DSL/Wasm/native), watchdogs, telemetry emitters, config client for MeshKit, and capability advertisement. Deployed per workload or as edge proxy.

### 1.4 References
- docs/knowledge-base/dotnet-performance-guidelines.md
- docs/architecture/omnirelay-rpc-mesh.md
- docs/architecture/transport-layer-vision.md

## 2. Overall Description
### 2.1 Product Perspective
- **System Interfaces**: Config watch (protobuf/xDS-like), artifact fetch (registry/HTTP/OCI optional), OTLP telemetry out, upstream TCP/HTTP connections, TLS/mTLS termination/origination.
- **User Interfaces**: CLI flags/env/config files for standalone; programmatic host builder APIs for in-proc embedding; admin endpoints for health/metrics.
- **Hardware Interfaces**: Network interfaces only; no special hardware.
- **Software Interfaces**: .NET 10 Native AOT runtime; optional Wasm runtime libraries; OS sockets/epoll/kqueue; TLS libraries from .NET.
- **Communication Interfaces**: HTTP/1.1, HTTP/2, gRPC; optional TCP passthrough; control-plane streams over gRPC; telemetry over OTLP/gRPC.
- **Memory Constraints**: AOT footprint minimized; extension heaps bounded per VM; request buffering bounded via watermarks.

### 2.2 Design Constraints
- Operations: must hot-reload config/extensions without restarts; per-worker isolation; no runtime codegen/Reflection.Emit.
- Site Adaptation: allow per-region/per-tenant configs and runtime selection of Wasm engine (V8 default, Wasmtime/WAMR if built in).

### 2.3 Product Functions
- L4/L7 proxying with routing, retries, timeouts, circuit breaking, outlier detection.
- Security: mTLS, JWT validation, RBAC; certificate management via MeshKit.
- Extensibility: DSL interpreter; Proxy-Wasm host; native plugin host via `NativeLibrary.Load` + `UnmanagedCallersOnly` ABI.
- Telemetry: metrics, structured logs, traces; tap/diagnostic hooks.
- Resilience: watchdogs, fail-open/closed/reload policies, LKG cache, capability negotiation.

### 2.4 User Characteristics
- Platform/SRE engineers, security engineers, and service developers familiar with .NET and mesh concepts; expect automation and low-touch ops.

### 2.5 Constraints, Assumptions, Dependencies
- Runs on Linux/macOS (dev) targeting Linux production; depends on MeshKit for dynamic config/identity when present; assumes signed artifacts; assumes per-RID AOT builds.

## 3. Specific Requirements
### 3.1 External Interface Requirements
- Control-plane gRPC: watch streams for routes/clusters/policies/extensions with versioned schemas and capability flags.
- Admin endpoints: health, metrics, config status, LKG info, extension state.
- Telemetry: OTLP exporters (gRPC/HTTP) with per-tenant rate limits.
- Extension host APIs: Proxy-Wasm ABI 0.2.x; native ABI function-pointer table; DSL host functions list.

### 3.2 Performance Requirements
- Target p99 overhead vs direct socket: minimal (define SLO per deployment mode); zero reflection in hot paths; pooled buffers; bounded allocations; quick cold start (AOT).
- Wasm boundary minimized via batching; watchdog limits prevent long pauses.

### 3.3 Logical Database Requirement
- No persistent DB; uses on-disk cache for LKG config and extension artifacts with integrity hashes.

### 3.4 Software System Attributes
- **Reliability**: LKG fallback, watchdogs for threads/VMs, fail-open/closed policies.
- **Availability**: hot reload without restart; per-worker isolation to contain crashes.
- **Security**: signed config/artifacts; mTLS; policy enforcement; least-privilege FS for extracted plugins.
- **Maintainability**: modular hosts (in-proc/sidecar/edge share core), capability negotiation, versioned schemas.
- **Portability**: per-RID AOT builds; configurable Wasm runtime; supports x86_64/ARM64.

### 3.5 Functional Requirements
#### Functional Partitioning
- Config subsystem, listener/pipeline, routing/cluster manager, extension hosts (DSL/Wasm/native), telemetry, watchdogs/capability reporting.

#### Functional Description
- Receive signed config delta/snapshot; validate capabilities; apply atomically per epoch.
- Accept connections; execute filter chain (including extensions); enforce policy; route to upstream; emit telemetry.
- Load extensions from signed packages; instantiate per worker; monitor resource usage; apply failure policy on fault.
- Expose admin/health endpoints; surface extension status and current config epoch.

#### Control Description
- State machines for config epochs (Desired, Staged, Active, LKG); watchdog actions (log -> restart VM -> fail-open/closed); filter-chain iteration with pause/continue semantics.

### 3.6 Environment Characteristics
- **Hardware**: commodity x86_64/ARM64; networked servers/containers.
- **Peripherals**: none beyond NICs.
- **Users**: operators, SREs, service developers.

### 3.7 Other
- Testing: xUnit suites; contract tests for control-plane schemas; soak tests for extension watchdog behavior; perf micro-benchmarks for boundary crossings.
- Deployment: supports in-proc, sidecar, headless edge; same ABI/config across modes; build artifacts signed and published per RID.
