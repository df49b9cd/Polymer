# WORK-008C – Telemetry Forwarding with Buffering/Backpressure

## Goal
Forward telemetry (OTLP) from nodes with bounded buffers and backpressure handling.

## Scope
- Buffering strategy, drop policies, and rate limits.
- Metrics for queue depth, drops, latency.

## Acceptance Criteria
- Under ingest backpressure, data loss bounded and observable; system remains responsive.

## Status
Done

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
