# WORK-004 – Deployment Packaging (In-Proc, Sidecar, Headless Edge)

## Goal
Deliver signed, per-RID Native AOT packages for all OmniRelay hosts with minimal footprints and clear host builders for in-proc, sidecar, and headless edge deployments.

## Scope
- Host wrappers for in-proc embedding, sidecar container, and headless edge binary.
- Per-RID publish pipelines (linux-x64/arm64; macOS dev) with symbol stripping and reproducible builds.
- Container images for sidecar/edge with slim base, non-root, readonly FS, and health endpoints.
- Packaging includes capability manifest (supported runtimes, limits, build epoch) for MeshKit.

## Requirements
1. **Signed artifacts** – All packages/images signed; signatures verified by MeshKit/agents before rollout.
2. **Reproducibility** – Deterministic builds with pinned toolchains; SBOM generated.
3. **Footprint** – Target minimal image size; document RAM/CPU expectations per mode.
4. **Interop** – Host selection via config/CLI flags; same config schema across modes.

## Deliverables
- Build scripts/pipelines producing per-RID artifacts and images.
- Capability manifest format and generator.
- Docs for selecting deployment mode and integrating with MeshKit rollout.

## Acceptance Criteria
- Reproducible build logs; SBOM produced; signatures validated in CI.
- Sidecar/edge images run as non-root with readonly FS; health endpoints live.
- In-proc host NuGet/package consumable by services; samples updated.

## Testing Strategy
- Pipeline integration tests verifying signatures and manifests.
- Runtime smoke tests per mode; health/readiness checks; config application.

## References
- `docs/architecture/OmniRelay.BRD.md`
- `docs/architecture/OmniRelay.SRS.md`

## Status
Needs re-scope (post-BRD alignment).
