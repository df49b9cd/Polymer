# WORK-008D – Resource/Security Hardening & Footprint

## Goal
Keep the agent lightweight and least-privilege while quantifying its resource footprint.

## Scope
- Run as non-root with minimal FS permissions; sandboxed paths.
- Measure CPU/memory under load; document limits.
- Add startup flags for resource caps if applicable.

## Acceptance Criteria
- Footprint measurements published; meets target ceilings.
- Security posture documented and validated in tests.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
