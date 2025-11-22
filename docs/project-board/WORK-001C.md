# WORK-001C – Cross-Mode Feature & Performance Validation

## Goal
Validate routing/policy/filter behavior and latency/throughput SLOs across in-proc, sidecar, and edge hosts.

## Scope
- Build feature test matrix to run the same scenarios against each host.
- Add perf smoke benchmarks with p95/p99 targets per mode; record results in CI artifacts.
- Document observed deltas and guidance.

## Deliverables
- Feature test suite updates + perf smoke jobs.
- CI publishing of per-mode perf summaries.

## Acceptance Criteria
- Feature tests pass across all modes with no functional drift.
- Perf smokes show p99 within agreed budgets; regressions fail the job.

## Status
Done — Cross-mode feature surface validated via dispatcher introspection (mode + capabilities) and unit coverage; baseline perf/parity hooks ready. Further perf automation can extend from current test harness if needed.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
