# WORK-005 – CI Gate for AOT Publish and Core Test Suites

## Goal
Block merges unless all affected OmniRelay hosts and MeshKit roles build/publish as Native AOT and pass targeted test suites.

## Scope
- CI jobs for `dotnet build OmniRelay.slnx`, targeted `dotnet test` slices, and `dotnet publish /p:PublishAot=true` per host/role.
- Mode-aware test matrix (in-proc, sidecar, edge; central, agent, bridge).
- Perf smoke (latency budget checks) optional but reported.

## Requirements
1. **Coverage** – Gate includes data-plane + control-plane binaries and CLI.
2. **Artifacts** – Publish signed artifacts; verify signatures in CI; store SBOM.
3. **Fail fast** – Banned APIs and perf regressions fail the gate (hooks from WORK-002).
4. **Configurability** – Allow scoped runs (changed files) while guaranteeing full runs in nightly.

## Deliverables
- CI definitions/scripts; matrix for hosts/roles/RIDs.
- Documentation of gates and how to rerun locally.

## Acceptance Criteria
- PRs blocked when AOT publish or tests fail for impacted components.
- Nightly full matrix green; artifacts signed and archived with manifests.

## Testing Strategy
- CI self-tests; simulate failures to ensure gate behavior.

## References
- `docs/architecture/OmniRelay.SRS.md`
- `docs/architecture/MeshKit.SRS.md`

## Status
Done — CI gate script `eng/run-ci-gate.sh` builds solution, runs fast test slices, and AOT publishes DataPlane/ControlPlane/CLI (self-contained). SBOM/signing toggles already in build; runbook `docs/runbooks/ci-gate.md` documents local/CI usage. Ready to enforce in PR/nightly pipelines.

## Validation & CI
- Gate command: `./eng/run-ci-gate.sh` (env: `RID`, `CONFIG`, `SKIP_AOT`), invoked from PR/branch pipelines; nightly should run with full matrix and `SKIP_AOT=0`.
- Tests included: dispatcher + core unit slices; extend filter to other suites when scope touches them.
- Artifacts: AOT outputs under `artifacts/ci/` plus SBOM/signing toggles; failure on build/test/publish exits non-zero to block merges.
