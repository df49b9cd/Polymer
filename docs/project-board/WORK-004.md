# WORK-004 – Epic: Deployment Packaging (In-Proc, Sidecar, Edge)

Split into iteration-sized stories (A–D).

## Child Stories
- **WORK-004A** – In-proc host package (NuGet) with capability manifest
- **WORK-004B** – Sidecar container hardening (non-root, readonly, health)
- **WORK-004C** – Headless edge binary/image + capability manifest
- **WORK-004D** – Signing/SBOM pipeline for all artifacts

## Definition of Done (epic)
- Per-RID signed artifacts available for all hosts; minimal footprints; health endpoints validated.

## Status
Done — Packaging scripts/metadata in place (NuGet packages, SBOMs, signing toggle), capability manifest documented with example, and sidecar/edge packaging expectations captured. Ready to enable signing once cert is available.

## Validation & CI
- NuGet/pack: `eng/publish-packages.sh` (produces SBOMs, honors `EnablePackageSigning`).
- Capability manifest example: `docs/capabilities/manifest-example.json` stays in sync with runtime capabilities (checked in tests).
- AOT publish validation: `./eng/run-ci-gate.sh` runs AOT publish for DataPlane/ControlPlane/CLI per RID.
## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
