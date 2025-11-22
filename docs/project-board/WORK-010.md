# WORK-010 – Epic: Extension Registry & Admission

Split into iteration-sized stories (A–D).

## Child Stories
- **WORK-010A** – Manifest schema & signing requirements
- **WORK-010B** – Storage + fetch/caching pipeline
- **WORK-010C** – Publish/list/promote CLI/APIs
- **WORK-010D** – Admission/compatibility checks (ABI/capabilities)

## Definition of Done (epic)
- Signed extension artifacts published, validated, and retrievable with compatibility guarantees and auditing.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
