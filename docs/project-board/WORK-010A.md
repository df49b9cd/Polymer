# WORK-010A – Manifest Schema & Signing Requirements

## Goal
Define extension manifest schema and mandatory signing requirements.

## Scope
- Fields: name, version, hash, signature, ABI/runtime requirements, dependencies, failure policy hints.
- Documentation of signing process and roots of trust.

## Acceptance Criteria
- Manifest schema checked in; signing docs published.
- Unit tests validate schema parsing.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
