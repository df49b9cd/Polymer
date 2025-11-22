# WORK-007D – Identity Audit, Metrics, and Alerts

## Goal
Add observability and auditability for identity operations (issue, renew, revoke).

## Scope
- Metrics for issuance latency, failure rates, time-to-expiry.
- Alerts for expiry risk and renewal failures.
- Audit log entries for identity actions.

## Acceptance Criteria
- Metrics/alerts live in dashboards; tests trigger alerts in staging.
- Audit logs include who/what/when for identity events.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
