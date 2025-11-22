# WORK-020A – Synthetic Probes for Control/Data Paths

## Goal
Implement read-only probes that exercise control APIs and data-plane endpoints, including downgrade detection.

## Scope
- Probe runner with tagged requests; rate limits.
- Downgrade and capability negotiation checks.

## Acceptance Criteria
- Probes run on schedule/CI; detect injected regressions; metrics emitted.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
