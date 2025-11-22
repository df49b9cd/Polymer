# WORK-021A – Chaos Scenario Catalog & Definitions

## Goal
Define deterministic chaos scenarios with expected outcomes and guardrails.

## Scope
- Scenario specs: control partition, bridge outage, agent loss, extension watchdog trip, registry/CA outage.
- Expected metrics/alerts and safety limits.

## Acceptance Criteria
- Catalog checked in; scenarios have clear success/fail signals and guardrails.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
