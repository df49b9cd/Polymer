# WORK-019B – Audit Logging Schema & Queries

## Goal
Define and implement audit logging for control mutations, rollouts, registry and identity actions.

## Scope
- Schema, sinks, retention; tamper-evidence.
- CLI/queries to retrieve audit records.

## Acceptance Criteria
- Audit entries produced for key actions; queryable via CLI/API; retention documented.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
