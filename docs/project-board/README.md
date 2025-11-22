# Project Board Overview (Aligned to OmniRelay & MeshKit BRD/SRS)

Use this board with:
- `docs/architecture/OmniRelay.BRD.md` / `docs/architecture/OmniRelay.SRS.md`
- `docs/architecture/MeshKit.BRD.md` / `docs/architecture/MeshKit.SRS.md`
- `docs/architecture/transport-layer-vision.md`

## Dependency Backbone

| Lane | Focus | Stories (sequence) | Notes |
| --- | --- | --- | --- |
| L0 | Shared foundations & split layers | WORK-023 → WORK-001 → WORK-005 | Shared packages in place (Codecs/Protos/Transport.Host) and runtime split completed (`OmniRelay.DataPlane`, `OmniRelay.ControlPlane`). Next: mode parity, AOT/perf hardening, extensions, packaging, and CI gating. |
| L1 | MeshKit control-plane foundation | WORK-006 → WORK-009 | Define control protocol, identity/CA, local agent with LKG cache, and bootstrap/watch harnesses. MeshKit consumes shared libraries/Transport.Host, not DataPlane internals. |
| L2 | Extensions & rollout | WORK-010 → WORK-011 | Signed extension registry plus rollout/kill-switch machinery for DSL/Wasm/native bundles. |
| L3 | Federation & capability | WORK-012 → WORK-016 | Telemetry correlation, domain bridging, capability down-leveling, routing/failover orchestration. |
| L4 | Ops, UX, and resilience | WORK-017 → WORK-022 | Operator UX/CLI, dashboards/alerts, security/audit, probes, chaos automation, samples/docs. |

Lanes are ordered; L0 must remain healthy to advance others. L1 depends on L0; L2 depends on L1; L3 depends on L1/L2; L4 spans all lanes for operability.

## Active Work Items

Status legend: Open / In design / In progress / Needs re-scope / Done. Epics are WORK-xxx; iteration-sized stories are suffixed (e.g., WORK-001A).

### L0 – OmniRelay Core & Perf

| ID | Title | Status | Notes |
| --- | --- | --- | --- |
| WORK-001 | OmniRelay transport/pipeline parity (in-proc, sidecar, edge) | Needs re-scope | Ensure identical behavior & perf targets across deployment modes with AOT-safe pipelines; now runs atop the split `OmniRelay.DataPlane` runtime. |
| WORK-002 | Native AOT perf & compliance baseline | Needs re-scope | Apply dotnet-performance-guidelines; measure/watch p99; enforce no reflection/JIT in hot paths. |
| WORK-003 | Extension hosts (DSL, Proxy-Wasm, native) + watchdogs | Needs re-scope | Sandbox, quotas, failure policies, and capability flags per runtime. |
| WORK-004 | Deployment packaging (per-RID, in-proc host, sidecar, headless edge) | Needs re-scope | Signed artifacts, slim images, host wrappers. |
| WORK-005 | CI gating for AOT/publish/tests | Open | Block merges unless all hosts build/publish AOT and core test tiers pass (DataPlane + ControlPlane). |

### L1 – MeshKit Control Plane

| ID | Title | Status | Notes |
| --- | --- | --- | --- |
| WORK-006 | Control protocol (xDS-like) & capability negotiation | Needs re-scope | Versioned protobufs, deltas/snapshots, epochs, capability flags; served by `OmniRelay.ControlPlane` and consumed by agents/edge. |
| WORK-007 | Identity/CA service & cert rotation | Needs re-scope | CSR, issuance, trust bundles, rotation, SPIFFE compatibility. |
| WORK-008 | Local agent with LKG cache & telemetry forwarder | Needs re-scope | Subscribe to control domain, cache LKG, renew certs, never elect leaders. |
| WORK-009 | Bootstrap/watch harness & validation | Needs re-scope | Shared startup harness, config validators, resume/backoff semantics. |

### L2 – Extensions & Rollout

| ID | Title | Status | Notes |
| --- | --- | --- | --- |
| WORK-010 | Extension registry (DSL/Wasm/native) + admission | Needs re-scope | Signed manifests, ABI metadata, dependency checks, storage. |
| WORK-011 | Rollout manager (canary/kill-switch/fail-open/closed) | Needs re-scope | Policy-driven rollouts with epoch tracking and remote disable. |

### L3 – Federation & Capability

| ID | Title | Status | Notes |
| --- | --- | --- | --- |
| WORK-012 | Telemetry/health correlation with config epochs | Needs re-scope | Ingest OTLP, map to versions, surface SLO/regressions. |
| WORK-013 | Mesh bridge/federation between control domains | Needs re-scope | Export allowlists, identity mediation, queued replay. |
| WORK-014 | Capability down-leveling & schema evolution | Needs re-scope | Tailor payloads to node capabilities; maintain backward compatibility windows. |
| WORK-015 | Routing/policy engine with multi-version canary | Needs re-scope | Compute routes/policies with staged rollout and verification. |
| WORK-016 | Cross-region/cluster failover orchestration | Needs re-scope | Planned/emergency failovers driven by MeshKit using OmniRelay transports. |

### L4 – Ops, UX, Resilience

| ID | Title | Status | Notes |
| --- | --- | --- | --- |
| WORK-017 | Operator UX/CLI (OmniRelay + MeshKit) | Needs re-scope | Unified CLI against control APIs; AOT-safe; table/JSON outputs. |
| WORK-018 | Dashboards & alerts | Needs re-scope | MeshKit CP health, OmniRelay DP signals, extension rollouts. |
| WORK-019 | Security, audit, supply-chain hardening | Needs re-scope | Signed artifacts/configs, audit trails, FS perms, key rotation. |
| WORK-020 | Synthetic probes & partition tests | Needs re-scope | Read-only probes, LKG verification, downgrade checks. |
| WORK-021 | Chaos automation | Needs re-scope | Scenarios for CP partition, agent loss, extension crash; nightly runs. |
| WORK-022 | Samples & docs alignment | Needs re-scope | In-proc vs sidecar examples; extension lifecycle; control-plane roles. |

## Working Agreements
- Keep OmniRelay data-plane lean and AOT-safe; MeshKit owns control-plane logic (identity, policy, registry, rollout, bridging).
- Signed artifacts/configs and capability negotiation are mandatory before loading extensions or applying policy.
- Every story lists target deployment modes (in-proc/sidecar/edge) and control-plane roles (central/agent/bridge) it touches.
- CI gates require `dotnet build OmniRelay.slnx`, relevant `dotnet test` slices, and AOT publish for affected hosts.
- Update docs/samples alongside behavior changes, especially for extension lifecycle and control-plane interaction.
