# WORK-021 – Epic: Chaos Automation

Split into iteration-sized stories (A–B).

## Child Stories
- **WORK-021A** – Chaos scenario catalog & definitions
- **WORK-021B** – Automation runner & reporting

## Definition of Done (epic)
- Repeatable chaos suite runs in staging/nightly covering CP partitions, agent loss, extension failures, and failover triggers.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
