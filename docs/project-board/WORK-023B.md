# WORK-023B – Packaging & Multi-Targeting

## Goal
Make the shared libraries NuGet-packable/internal-feed ready, multi-targeting `net10.0` and AOT-safe `net10.0` with `#if NATIVE_AOT` guards, and publish them for consumption by OmniRelay.DataPlane/ControlPlane and MeshKit builds.

## Scope
- NuGet metadata, internal feed publishing, and versioning strategy.
- Multi-targeting; guard AOT-sensitive code paths; avoid reflection/dynamic load.
- CI jobs to build/package and push these libraries; OmniRelay.DataPlane, OmniRelay.ControlPlane, and MeshKit consume via project/package refs (no source links).
- Generate SBOMs (SPDX + CycloneDX) on pack/publish; keep signing toggleable once a cert is available.

## Acceptance Criteria
- Packages publish successfully for both targets; AOT publish passes for DataPlane and ControlPlane hosts using only package references.
- SBOMs (SPDX & CycloneDX) produced for package artifacts.
- Package signing enabled when org cert is available (toggle present in build).

## Status
Done — Shared libraries (Protos, Codecs, Transport, ControlPlane.Abstractions) are packable with symbols and SBOMs (SPDX + CycloneDX). Packaging script `eng/publish-packages.sh` builds Release + packs to `artifacts/packages`. Signing toggle is wired via `EnablePackageSigning` (off until cert exists).

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
