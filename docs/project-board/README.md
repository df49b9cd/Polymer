# Project Board Overview

OmniRelay now plays the role of a transport appliance layered beneath MeshKit (control plane) and Hugo (execution/runtime). Only stories that advance this model remain on the board—completed epics have been removed. Use this document alongside:
- `docs/architecture/transport-layer-vision.md` for the layered narrative.
- `docs/project-board/transport-layer-plan.md` for phase sequencing and validation gates.

## Dependency Backbone

| Lane | Focus | Stories (sequence order) | Notes |
| --- | --- | --- | --- |
| L0 | Transport correctness & AOT | `WORK-001` → `WORK-005` | Keep OmniRelay transport-only (WORK-001) and finish AOT baseline/tooling/CI (WORK-002..WORK-005) before layering new modules. |
| L1 | MeshKit Core surfaces | `WORK-006` → `WORK-009` | Provide shared client helpers, chaos/probe infra, config watchers, and bootstrap harnesses reused by every MeshKit module. |
| L2 | MeshKit Registry & Shards | `WORK-010` → `WORK-014` | Build MeshKit.Shards, MeshKit.Rebalancer, and MeshKit.Registry APIs/mutations on top of OmniRelay transports. |
| L3 | Multi-cluster & failover | `WORK-015`, `WORK-016` | Layer MeshKit.ClusterDescriptors and MeshKit.Failover once registry + rebalancer events exist. |
| L4 | Operator experience & docs | `WORK-017` → `WORK-022` | Refresh docs, dashboards, CLI, probes, and chaos/automation around the new module boundaries. |

Lanes still respect the transport-layer plan: L0 must stay green before advancing, L1 unblocks L2, L2/L3 feed L4.

## Active Work Items

### OmniRelay & Platform Readiness

| ID | Title | Status | Notes |
| --- | --- | --- | --- |
| WORK-001 | OmniRelay Transport & Encoding Policy Engine | Complete | HTTP/3-first policy engine enforced at startup + CLI with downgrade summaries/telemetry. |
| WORK-002 | OmniRelay AOT Compliance Baseline | Complete | Native AOT surfacing trimmed/sourced diagnostics, shard controls, bootstrap paths; publish gated by symbol-strip tool availability. |
| WORK-003 | MeshKit Library AOT Readiness | Open | Ensure all MeshKit modules (shards, rebalancer, registry, cluster) are trimming-safe/native-publishable. |
| WORK-004 | Native AOT Tooling & Packaging | In design | Deliver native AOT CLI/tooling for Linux/macOS/Windows plus slim containers. |
| WORK-005 | AOT CI Gating & Runtime Validation | Open | CI blocks merges unless OmniRelay, MeshKit, and CLI native builds + smoke tests succeed. |
| WORK-006 | CLI & Diagnostics Client Helpers | In design | Shared HTTP/3/gRPC client SDK that powers CLI, MeshKit modules, and third-party automation. |
| WORK-007 | Chaos & Health Probe Infrastructure | In design | Reusable probe scheduler/chaos hooks aligned with MeshKit synthetic/chaos stories. |
| WORK-008 | Configuration Reload & Watcher Services | In design | Shared configuration watchers/validators for OmniRelay transports + MeshKit modules. |
| WORK-009 | Runtime Bootstrap Harness | In design | Common startup harness (logging, telemetry, feature flags) for OmniRelay + MeshKit hosts. |

### MeshKit Modules & Operator Experience

| ID | Title | Status | Notes |
| --- | --- | --- | --- |
| WORK-010 | MeshKit.Shards APIs & Tooling | Open | Re-scope shard APIs/CLI to live inside MeshKit.Shards while OmniRelay remains transport-only. |
| WORK-011 | MeshKit.Rebalancer | Open | Health-aware rebalancer consuming MeshKit.Shards + observability bus. |
| WORK-012 | MeshKit.Rebalance Observability | Open | Dashboards/alerts sourced from MeshKit.Rebalancer metrics; OmniRelay only exposes transport counters. |
| WORK-013 | MeshKit.Registry Read APIs | Open | Versioned HTTP/3/gRPC surfaces for peers/clusters/config derived from MeshKit registry state. |
| WORK-014 | MeshKit.Registry Mutation APIs | Open | RBAC-governed mutation verbs plus CLI adapters targeting MeshKit endpoints. |
| WORK-015 | MeshKit.Cluster Descriptors | Open | First-class cluster metadata/priorities for multi-region routing and failover automation. |
| WORK-016 | MeshKit.Cross-Cluster Failover | Open | Planned/emergency workflows orchestrated via MeshKit automation using OmniRelay transports. |
| WORK-017 | Samples & Docs Refresh | Open | Update samples/docs to showcase Hugo → OmniRelay → MeshKit flow, including new control surfaces. |
| WORK-018 | Operator Dashboards & Alerts | Open | Unified dashboards fed by MeshKit metrics (gossip, leadership, shards, rebalancer, replication). |
| WORK-019 | OmniRelay Mesh CLI Enhancements | In design | CLI focuses on MeshKit endpoints (shards, clusters, transport stats) while keeping transport tooling deterministic/AOT safe. |
| WORK-020 | MeshKit Synthetic Health Checks | Open | Read-only probes hitting MeshKit APIs/streams plus HTTP/3 negotiation tests. |
| WORK-021 | MeshKit Chaos Environments | Open | Deterministic docker/Kubernetes chaos stacks focused on MeshKit modules. |
| WORK-022 | MeshKit Chaos Automation & Reporting | Open | Scenario DSL + CI automation enforcing nightly chaos runs tied to MeshKit metrics. |

## Working Agreements
- Every story **must** cite the layer it belongs to (Hugo, OmniRelay, MeshKit) and consume shared transport/telemetry kits instead of inventing new hosting code.
- Native AOT publish + `dotnet test` (unit, integration, feature, hyperscale tiers) remain part of acceptance criteria and are enforced by WORK-002 → WORK-005.
- CLI/SDK/diagnostics endpoints are the canonical control surfaces. Third-party modules must integrate through those instead of accessing private registries.
- Update this board whenever a story lands so completed epics stay archived outside the board.
