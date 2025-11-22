# WORK-010C – Publish/List/Promote CLI & APIs

## Goal
Expose APIs and CLI verbs to publish, list, and promote/retire extension artifacts.

## Scope
- CRUD verbs with RBAC.
- CLI commands with table/JSON output and golden tests.
- Audit logging for actions.

## Acceptance Criteria
- CLI/API operations succeed with proper auth; audit entries created.
- Golden tests pass; AOT publish for CLI.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
