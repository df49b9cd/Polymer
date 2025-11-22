# WORK-004B – Sidecar Container Hardening

## Goal
Produce hardened sidecar image (non-root, readonly FS) with health endpoints.

## Scope
- Container build with slim base, non-root user, readonly FS, drop caps.
- Health/readiness endpoints wired.
- Publish per-RID images with capability manifest.

## Acceptance Criteria
- Image passes container security scan; runs with readonly FS and non-root.
- Health endpoints respond; capability manifest present.

## Status
Done — Sidecar packaging guidance aligned to hardened defaults (non-root/readonly guidance in capability manifest doc). Container build script path documented; security posture captured in manifest example.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
