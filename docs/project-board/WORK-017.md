# WORK-017 – Epic: Operator UX & CLI

Split into iteration-sized stories (A–C).

## Child Stories
- **WORK-017A** – Core control/rollout commands
- **WORK-017B** – Diagnostics & capability inspection commands
- **WORK-017C** – Packaging, completions, and AOT publish

## Definition of Done (epic)
- CLI covers core control/diagnostic flows, AOT-safe, with tested outputs and packaging.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
