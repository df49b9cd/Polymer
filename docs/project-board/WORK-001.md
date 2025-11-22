# WORK-001 – OmniRelay Transport & Pipeline Parity (In-Proc, Sidecar, Edge)

## Goal
Guarantee identical semantics, policy enforcement, and performance baselines for OmniRelay across all deployment modes (in-process library, sidecar, headless edge proxy) while staying compliant with Native AOT constraints.

## Scope
- Listener/pipeline configuration parity and deterministic filter ordering across modes.
- Routing, retry, timeout, and circuit-breaking behavior equivalence with mode-specific defaults documented.
- Buffering/watermark and watchdog defaults tuned per mode.
- Capability advertisement of supported modes and limits back to MeshKit.

## Requirements
1. **Parity** – Same config yields the same behavior in every mode; any deviation must be reported via capability flags and warnings.
2. **Performance** – Document and meet SLOs (p95/p99) per mode; avoid reflection/JIT in hot paths (AOT-safe).
3. **Isolation knobs** – Support per-mode watchdog thresholds and buffer limits; fail-open/closed configurable.
4. **Admin visibility** – Surface mode, config epoch, capability set, and active filters via admin/metrics endpoints.
5. **Testing coverage** – Cross-mode feature tests validate parity and latency deltas within agreed budgets.

## Deliverables
- Unified configuration schema covering all deployment modes.
- Admin/diagnostic surfaces exposing mode, capabilities, and filter chain.
- Benchmarks comparing modes with published SLOs and guidance.
- Updated docs/samples showing how to select modes per workload.

## Acceptance Criteria
- A single config file applies without edits to all modes; differences are limited to documented capability flags.
- Benchmarks demonstrate p99 within target budgets per mode; regressions fail the gate.
- Feature tests pass for in-proc, sidecar, and edge hosts.
- Native AOT publish succeeds for all hosts.

## Testing Strategy
- Unit: configuration parsing, capability flags, watchdog/buffer defaults per mode.
- Integration: run identical configs across hosts; assert routing/policy outcomes match.
- Feature: end-to-end HTTP/gRPC flows per mode with latency assertions.
- Perf: compare p95/p99 latency/throughput; track in CI trend.

## References
- `docs/architecture/OmniRelay.BRD.md`
- `docs/architecture/OmniRelay.SRS.md`
- `docs/architecture/transport-layer-vision.md`

## Status
Needs re-scope (post-BRD alignment).
