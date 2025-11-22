# WORK-020 – Epic: Synthetic Probes & Partition Tests

Split into iteration-sized stories (A–B).

## Child Stories
- **WORK-020A** – Synthetic probes for control/data paths
- **WORK-020B** – Partition simulations and LKG verification

## Definition of Done (epic)
- Probes continuously validate control/data health; partition simulations verify LKG and recovery.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
