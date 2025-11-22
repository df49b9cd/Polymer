# WORK-018 – Epic: Dashboards & Alerts

Split into iteration-sized stories (A–B).

## Child Stories
- **WORK-018A** – Control-plane dashboards/alerts
- **WORK-018B** – Data-plane & extension dashboards/alerts

## Definition of Done (epic)
- Actionable dashboards/alerts for CP/DP with epoch/rollout context and runbook links.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
