# WORK-006B – Watch Streams (Deltas/Snapshots) with Resume/Backoff

## Goal
Implement control watch streams over gRPC supporting deltas, snapshots, resume tokens, and backoff guidance.

## Scope
- Server streaming endpoints; client logic for subscribe/resume.
- Backoff policy and retry limits; LKG fallback signaling.

## Acceptance Criteria
- Integration tests cover connect/drop/resume without missing epochs.
- Backoff behavior observable via metrics/logs.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
