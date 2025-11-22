# MeshKit Business Requirements Document

## 1. Introduction
### 1.1 Purpose
Capture the business objectives and scope for MeshKit as the control-plane platform that computes, signs, and distributes policy, routing, identity, and extension artifacts to OmniRelay deployments at global scale.

### 1.2 Scope
- In scope: control-plane computation, config/watch delivery, identity/CA services, extension registry and rollout, capability negotiation, multi-role topology (centralized, local agent, mesh bridge), observability of data-plane health. MeshKit runs on the `OmniRelay.ControlPlane` runtime and consumes shared OmniRelay libraries (Codecs/Protos/Transport.Host) but does not embed the data-plane filter/peer stack.
- Out of scope: serving application traffic directly; hosting business logic; UI beyond operator/API/CLI surfaces as defined.

### 1.3 Background
- MeshKit orchestrates OmniRelay instances across regions/tenants, enabling zero-trust networking, policy uniformity, and rapid rollout/rollback of extensions and routes.

### 1.4 References
- docs/architecture/omnirelay-rpc-mesh.md
- docs/architecture/service-discovery.md
- docs/architecture/meshkit-extraction-plan.md

### 1.5 Assumptions and Constraints
- MeshKit must operate even with partial connectivity; local agents cache LKG config.
- All configs/artifacts are signed; control domains are isolated by epoch/term; leader election/gossip limited to a control domain.
- Needs to serve heterogeneous OmniRelay capabilities (in-proc, sidecar, Wasm runtime variants).

### 1.6 Document Overview
Defines business needs, stakeholders, functional expectations, and operational requirements that inform the MeshKit SRS.

## 2. Methodology
- Derive requirements from service-mesh control-plane patterns, global-scale constraints, and the desire to reuse OmniRelay transports/codecs without duplicating data-plane logic in the CP.

## 3. Functional Requirements
### 3.1 Context
- MeshKit computes desired state (routes, clusters, policies, extensions, certificates) and distributes it to OmniRelay nodes via watch streams; local agents cache and enforce LKG during partitions; mesh bridges federate selective state across domains.

### 3.2 User Requirements
- Platform/SRE: safe canary/rollback, blast-radius control, observability of rollout health, emergency kill switches.
- Security/compliance: CA/CSR handling, mTLS identities, signed artifacts, auditability, sovereignty-aware federation.
- Service teams: predictable policy/application of routes, minimal CP-induced downtime, clear capability negotiation.

### 3.3 Data Flow Diagrams (textual)
- Control domain: operators/API -> MeshKit central (leader ring) -> signed config -> watch stream to local agents -> OmniRelay instances.
- Federation: MeshKit bridge subscribes to domain A, filters/rewrites, publishes to domain B with distinct epochs/IDs.
- Identity: OmniRelay -> agent -> MeshKit CA (CSR) -> cert + trust bundle -> OmniRelay.

### 3.4 Logical Data Model / Data Dictionary
- Policy bundle: routes, clusters, authz rules, retries/CB, extension bindings, epoch/version, capability targets.
- Identity objects: CSR requests, issued certs, trust bundles, rotation schedule.
- Extension artifact: manifest + signature + rollout policy (canary scope, fail-open/closed, dependencies).
- Telemetry envelope: metrics/logs/traces with node metadata, version, tenant/region tags.

## 4. Other Requirements
### 4.1 Interface Requirements
- Northbound API/CLI for operators; webhooks/GitOps integration optional.
- Southbound gRPC watch APIs to OmniRelay/local agents; artifact registry (OCI/HTTP) with signed blobs.

### 4.2 Data Conversion Requirements
- Versioned schemas with backward compatibility; automated validators and simulators before publish; signed deltas and snapshots.

### 4.3 Hardware/Software Requirements
- Deployable on Linux containers/VMs; relies on persistent store for state (pluggable); requires network reachability to agents; supports x86_64/ARM64.

### 4.4 Operational Requirements
- Leader election/gossip confined to each control domain; bridges do not merge consensus rings.
- LKG caching at agents for partition survival; explicit epoch/term markers for reconciliation.
- Canary and rollback workflows for config and extensions; remote kill switch per extension/policy.
- Audit logging for config/artifact changes; rate-limited telemetry ingestion.
