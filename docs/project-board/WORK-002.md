# WORK-002 – Native AOT Performance & Compliance Baseline

## Goal
Establish and enforce the Native AOT performance and correctness baseline for OmniRelay hosts (in-proc, sidecar, edge) with automated verification.

## Scope
- Apply `docs/knowledge-base/dotnet-performance-guidelines.md` to all data-plane binaries.
- Remove/guard reflection, dynamic loading, and JIT-only APIs in hot paths.
- Instrument perf counters (alloc rate, latency, CPU) per filter stage and per extension type.
- Define p95/p99 SLOs per mode and bake thresholds into CI.

## Requirements
1. **AOT safety** – No `Assembly.Load`, `Expression.Compile` with IL gen, or runtime codegen in DP paths.
2. **Perf instrumentation** – Standard counters/timers for routing, filter chain, extensions; export via OTLP.
3. **Budgets** – Publish SLOs and enforce in perf tests; failures block merges.
4. **Docs** – Update knowledge-base with AOT gotchas and perf tuning guidance.

## Deliverables
- Analyzer/CI checks for banned APIs; suppression process documented.
- Perf dashboards and thresholds used by CI gates.
- Updated perf guidelines with OmniRelay-specific examples.

## Acceptance Criteria
- CI fails on banned APIs or SLO regressions.
- Perf dashboards show stable baselines for each mode.
- Native AOT publish succeeds across RIDs used in CI (linux-x64, linux-arm64, macOS-dev).

## Testing Strategy
- Static analysis for forbidden APIs.
- Microbenchmarks for filter chain and extension boundaries.
- Perf regression tests per mode with SLO assertions.

## References
- `docs/knowledge-base/dotnet-performance-guidelines.md`
- `docs/architecture/OmniRelay.SRS.md`

## Status
Needs re-scope (post-BRD alignment).
