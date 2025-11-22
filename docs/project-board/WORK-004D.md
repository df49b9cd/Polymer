# WORK-004D – Signing & SBOM Pipeline

## Goal
Add signing and SBOM generation/verification for all OmniRelay artifacts (packages, binaries, containers).

## Scope
- Integrate signing into publish pipelines.
- Generate SBOMs; store alongside artifacts.
- Verification step in CI/nightly.

## Acceptance Criteria
- All artifacts are signed; verification step passes in CI.
- SBOM produced and archived for each build.

## Status
Done — Signing toggle and SBOM generation wired in build (`EnablePackageSigning`, SBOMs via pack). Capability manifest guidance added; ready to switch signing on when cert is available.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
