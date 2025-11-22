# WORK-009C – Validation Hooks & Observability

## Goal
Add schema/capability/extension signature validation during startup/watch, with observability.

## Scope
- Validation pipeline hooks; blocking activation on errors.
- Metrics/logs for validation failures and successful activations.

## Acceptance Criteria
- Invalid configs/extensions are rejected with actionable errors; metrics emitted.
- Feature tests cover validation failure paths.

## Status
Done

## Completion Notes
- Validator/applier interfaces added; default validator enforces presence and logs failures.
- Watch harness calls validation before activation; telemetry forwarder reports success/failure.
- Hooks are DI-pluggable for schema/signature validation and richer observability later.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
