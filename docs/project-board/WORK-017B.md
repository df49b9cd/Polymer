# WORK-017B – Diagnostics & Capability Inspection Commands

## Goal
Add CLI commands for capability sets, config epoch/LKG status, extension health, and control-stream health.

## Scope
- Read-only diagnostic verbs; downgrade detection; exit codes.
- Golden tests for output.

## Acceptance Criteria
- Diagnostics run against fixtures for all roles/modes; outputs verified.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
