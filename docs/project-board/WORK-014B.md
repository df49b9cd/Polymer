# WORK-014B – Tailoring Engine for Payloads

## Goal
Tailor control payloads to node capabilities (e.g., omit unsupported runtimes/features).

## Scope
- Server-side selection logic; fallbacks.
- Errors when required capability missing.

## Acceptance Criteria
- Mixed capability tests show correct tailoring or graceful rejection.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
