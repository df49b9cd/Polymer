# WORK-018A – Control-Plane Dashboards & Alerts

## Goal
Dashboards/alerts for MeshKit central/agent/bridge health (stream lag, CA status, rollout state).

## Scope
- Panels and alerts for control streams, leadership, CA expiry, agent status, rollout state.
- Runbook links.

## Acceptance Criteria
- Alerts fire in synthetic control-plane regressions; dashboards load within targets.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
