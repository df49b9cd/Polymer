# WORK-002B – Performance Baselines & Published SLOs

## Goal
Measure and publish p95/p99 latency/throughput baselines per deployment mode and key paths (routing, filter chain, extensions).

## Scope
- Micro/meso benchmarks for representative workloads.
- Capture alloc rate/CPU counters; store baseline artifacts.
- Publish SLO targets and guidance in knowledge-base.

## Deliverables
- Benchmark suite updates and run scripts.
- Baseline report checked into repo or artifacts.
- Docs with SLOs and tuning tips.

## Acceptance Criteria
- Benchmarks run and produce baselines for in-proc, sidecar, edge.
- SLOs documented and approved by stakeholders.

## Status
Done — Initial perf/SLO baselines documented in `docs/perf/perf-baseline.md`; perf smoke hook (`eng/run-perf-smoke.sh`) is available to run after tests. Benchmark harness to extend later with detailed metrics.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
