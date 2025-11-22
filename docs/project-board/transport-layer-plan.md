# Delivery Plan (Aligned to OmniRelay & MeshKit BRD/SRS)

Use with the project-board README and the OmniRelay/MeshKit BRD & SRS documents.

## Phase 0 – Alignment & Shared Foundations (Week 0–1)
- Socialize roles: OmniRelay = data plane (in-proc/sidecar/edge), MeshKit = control plane (central/agent/bridge).
- Tag all WORK items with deployment modes and control-plane roles they affect.
- Freeze new scope that mixes control-plane logic into OmniRelay.
- **Do first:** WORK-023 (shared transport/codec/proto libraries) to avoid duplicating hot-path code before other work builds on it.

## Phase 1 – OmniRelay Core & Perf (Weeks 1–4)
- WORK-001: Pipeline parity across modes; perf baselines per SLO; watchdog defaults.
- WORK-002: AOT/perf compliance (no reflection/JIT hot paths) and instrumentation.
- WORK-003: Extension hosts (DSL, Wasm, native) with quotas/failure policy; capability flags.
- WORK-004: Packaging per RID (in-proc host, sidecar, edge) with signed artifacts.
- WORK-005: CI gate for AOT publish + core test suites.

## Phase 2 – MeshKit Control Foundations (Weeks 3–7)
- WORK-006: Versioned control protocol (xDS-like) with deltas/snapshots and capability negotiation.
- WORK-007: Identity/CA service with CSR, issuance, rotation, and trust-bundle delivery.
- WORK-008: Local agent (LKG cache, telemetry forwarder, cert renewal) and subscription semantics.
- WORK-009: Bootstrap/watch harness, validators, resume/backoff rules.

## Phase 3 – Extensions & Rollout (Weeks 6–9)
- WORK-010: Extension registry/admission for signed DSL/Wasm/native bundles.
- WORK-011: Rollout manager with canary, fail-open/closed, kill switch, and epoch tracking.

## Phase 4 – Federation & Capability (Weeks 8–12)
- WORK-012: Telemetry/health correlation with config epochs; SLO regression detection.
- WORK-013: Mesh bridge/federation with export allowlists and replay during partition.
- WORK-014: Capability down-leveling; schema evolution windows and compatibility tests.
- WORK-015: Routing/policy engine with multi-version canary + verification hooks.
- WORK-016: Cross-region/cluster failover orchestration driven by MeshKit automation.

## Phase 5 – Ops, UX, Resilience (Weeks 10–14)
- WORK-017: Operator CLI/UX (AOT-safe) with table/JSON output, targeting control APIs.
- WORK-018: Dashboards/alerts for CP health, DP health, extension rollouts.
- WORK-019: Security/compliance/audit trails and supply-chain hardening.
- WORK-020: Synthetic probes + partition tests (downgrade, LKG verification).
- WORK-021: Chaos automation suites (extension crash, CP partition, agent loss).
- WORK-022: Samples/docs for in-proc vs sidecar vs edge, extension lifecycle, control roles.

## Validation Checklist
- Build/Test: `dotnet build OmniRelay.slnx`; targeted `dotnet test` slices for modified components.
- Native AOT: publish all affected hosts (OmniRelay in-proc/sidecar/edge; MeshKit central/agent/bridge; CLI) per WORK-005 gate.
- Security: signed config/artifacts verified before load; capability negotiation exercised in tests.
- Resilience: LKG cache and fail-open/closed paths exercised in feature/chaos suites.

## Exit Criteria
- OmniRelay delivers the same policy/enforcement across deployment modes with AOT/perf SLOs met and extension sandboxes enforced.
- MeshKit central/agent/bridge roles operate with signed config, identity, registry, rollout, and federation flows validated.
- Operators have CLI, dashboards, probes, and chaos automation tied to config epochs and capability flags.
