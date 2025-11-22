# WORK-002 – Epic: Native AOT Performance & Compliance

Split into iteration-sized stories (A–C) to keep changes safe and shippable each sprint.

## Child Stories
- **WORK-002A** – Ban-list analysis & AOT safety guards
- **WORK-002B** – Perf baselines and published SLOs
- **WORK-002C** – CI perf/SLO gating

## Definition of Done (epic)
- AOT-safe code paths verified; perf SLOs measured and enforced in CI; documentation updated.

## Status
Done — Banned API enforcement + guidance in place, perf/SLO baselines documented, perf smoke/gate hooks added for CI.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
