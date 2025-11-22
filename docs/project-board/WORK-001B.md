# WORK-001B – Admin/Diagnostics Parity Across Modes

## Goal
Expose consistent admin/diagnostic surfaces (mode, epoch, filter chain, capabilities) for in-proc, sidecar, and edge hosts.

## Scope
- Align admin endpoints/metrics emitted per mode.
- Ensure mode metadata (mode, capability set, build epoch) is exposed uniformly.
- Add smoke tests that query admin endpoints for each host flavor.

## Deliverables
- Admin endpoint updates and metrics additions.
- Docs: “Admin surfaces by deployment mode.”

## Acceptance Criteria
- Admin endpoints return comparable payloads across modes; differences documented.
- Integration tests query admin endpoints for all modes and pass.
- AOT publish green for modified hosts.

## Status
Done — Admin/introspection now surfaces deployment mode and capability flags consistently across modes via `/omnirelay/introspect`. Payload parity maintained; differences documented via capability entries.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
