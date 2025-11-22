# WORK-008B – Control Stream Client (Resume/Backoff)

## Goal
Implement control-plane subscription with resume tokens and backoff, without participating in leader election.

## Scope
- gRPC client for deltas/snapshots; resume token handling.
- Backoff policy with metrics/logs.

## Acceptance Criteria
- Disconnect/reconnect tested; no missed epochs; backoff observable.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
