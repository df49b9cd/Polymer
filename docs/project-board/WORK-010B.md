# WORK-010B – Storage & Fetch/Caching Pipeline

## Goal
Implement extension storage (OCI/HTTP) with integrity verification and agent caching.

## Scope
- Upload/download endpoints; hash/signature verification on fetch.
- Agent caching with eviction policy.

## Acceptance Criteria
- Fetch rejects tampered artifacts; cache hits/misses observable.
- Integration tests cover publish->fetch->cache workflows.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
