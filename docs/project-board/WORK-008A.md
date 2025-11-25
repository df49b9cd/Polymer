# WORK-008A – LKG Cache & Signature Validation

## Goal
Persist and validate last-known-good config/artifacts for use during partitions.

## Scope
- On-disk cache format with hashes/signatures.
- Load/verify on startup before activation.
- Admin/metrics for cache status.

## Acceptance Criteria
- Corrupted/unsigned cache rejected; healthy cache applied when control is offline.
- Tests cover save/load/validate flows.

## Status
Done

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
