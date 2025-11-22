# WORK-012A – Telemetry Tagging & Ingest Pipeline

## Goal
Ingest OTLP metrics/logs/traces and ensure each record is tagged with node ID, capability set, and config epoch/stage.

## Scope
- Configure collector/processor; enforce tagging; reject or downgrade untagged records.
- Rate limits and buffering.

## Acceptance Criteria
- Tagged telemetry observed in storage; untagged records handled per policy.
- Load test shows stable ingest with backpressure behavior documented.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
