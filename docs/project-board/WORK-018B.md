# WORK-018B – Data-Plane & Extension Dashboards/Alerts

## Goal
Dashboards/alerts for OmniRelay data-plane health and extension watchdogs, segmented by mode and epoch.

## Scope
- Latency/error panels per mode; extension watchdog/failure metrics.
- Alerts for SLO breach, watchdog hits, capability mismatches, LKG fallback events.

## Acceptance Criteria
- Alerts validated with synthetic events; dashboards show epoch/rollout context.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
