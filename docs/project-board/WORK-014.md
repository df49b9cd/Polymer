# WORK-014 – Epic: Capability Down-Leveling & Schema Evolution

Split into iteration-sized stories (A–C).

## Child Stories
- **WORK-014A** – Capability model & advertisement
- **WORK-014B** – Tailoring engine for payloads
- **WORK-014C** – Compatibility checker & deprecation policy

## Definition of Done (epic)
- Mixed-version fleets receive compatible payloads; incompatibilities detected early; policy documented.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
