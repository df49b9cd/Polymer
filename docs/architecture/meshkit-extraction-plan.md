# MeshKit Extraction Plan

This document originally described an external-repo extraction. Control-plane code is now already isolated inside this repo as `src/OmniRelay.ControlPlane` and `src/OmniRelay.ControlPlane.Abstractions`, with data-plane runtime in `src/OmniRelay.DataPlane` and shared packages (`Transport`, `Codecs`, `Protos`). The remaining purpose of this plan is to keep dependency boundaries clean and leave the door open for a future repo split if/when governance requires it.

## 1. Current Footprint

### Source directories to migrate (now already compartmentalized)
- `src/OmniRelay.ControlPlane/**` – hosting builders, control-plane services, clients, throttling/upgrade, shard/leadership.
- `src/OmniRelay.ControlPlane.Abstractions/**` – contracts for event bus, TLS options, drain participants.
- `src/OmniRelay.DataPlane/**` – stays data-plane only; do **not** reintroduce control-plane sources here.
- `src/OmniRelay.Diagnostics.*` – shared diagnostics runtime the MeshKit hosts depend on.
- `src/OmniRelay.ResourceLeaseReplicator.*` – replicator frameworks that feed rebalancer/failover plans.

### Tests to migrate
- `tests/OmniRelay.Core.UnitTests` (gossip, leadership, shards).
- `tests/OmniRelay.IntegrationTests` portions covering shard/leadership control planes.
- `tests/OmniRelay.FeatureTests` + `tests/OmniRelay.HyperscaleFeatureTests` scenarios tied to control-plane hosts (shards, leadership, chaos, failover).
- `tests/TestSupport` utilities used by the feature/hyperscale harness.

### Shared tooling
- CLI code paths under `src/OmniRelay.Cli` that call MeshKit endpoints.
- `docs/project-board/WORK-010..WORK-022` briefs and the knowledge-base entries in `docs/knowledge-base/meshkit/*`.

## 2. Pre-Extraction Checklist
1. **Code ownership tags** – Add `[MeshKit]` prefixes to namespaces/classes that will move so sweeping is easy (e.g., `namespace MeshKit.Shards` once we are ready).
2. **Public contract audit** – Freeze public request/response models for shards, leadership, rebalancer, and replication. Document versioning strategy in each WORK brief.
3. **Package identifiers** – Decide final NuGet IDs (e.g., `MeshKit.Shards`, `MeshKit.Rebalancer`, `MeshKit.Diagnostics`). Update `.csproj` metadata but keep existing names until extraction day.
4. **CLI split** – Build abstraction in `OmniRelay.Cli` so transport-only commands (e.g., `config validate`, `transport stats`) reside in OmniRelay, while MeshKit commands read from a shared SDK (WORK-006). This reduces circular dependencies later.
5. **Shared assets** – Move cross-cutting pieces (Diagnostics runtime, Telemetry kit, Config watcher, Bootstrap harness) into neutral packages before extraction (WORK-006..WORK-009).

## 3. Repository Split Steps
1. **Create new repo skeleton** (`meshkit/`):
   - `/src/MeshKit.*` for control-plane libraries.
   - `/tests/MeshKit.*` mirroring current test suites.
   - `/docs/` containing the WORK briefs relevant to MeshKit.
2. **Copy source folders** listed above into the new repo, preserving history (use `git subtree split` or `git filter-repo`).
3. **Adjust namespaces** to `MeshKit.*` where applicable and update `.csproj` names/AssemblyInfo.
4. **Re-wire OmniRelay repo**:
   - Remove moved projects from `OmniRelay.slnx`.
   - Add package references to the new MeshKit NuGet packages (once published) for components OmniRelay still needs (e.g., diagnostics runtime if we keep it in MeshKit; or move diagnostics to a neutral `OmniRelay.Diagnostics.*` package hosted in OmniRelay).
   - Update CLI references to pull MeshKit SDK from the new repo.

## 4. Build & CI Adjustments
- **New MeshKit CI**:
  - Restore/build MeshKit solution, run unit + integration + feature/hyperscale suites.
  - Run native AOT builds/tests (equivalent of WORK-002..WORK-005) for MeshKit-specific binaries.
  - Publish MeshKit packages and container images as artifacts.
- **OmniRelay CI updates**:
  - Remove control-plane test steps no longer in repo.
  - Add dependency restore step pulling MeshKit packages from internal feed/nuget.org.
  - Keep transport-only native AOT gate (WORK-002) intact.
- **Shared scripts** (`eng/run-aot-publish.sh`, `eng/run-ci.sh`):
  - Factor out MeshKit targets into separate scripts so each repo maintains its own list of projects.

## 5. Release & Versioning
- Adopt semantic versioning for MeshKit packages (e.g., `0.1.x` while in preview, align with OmniRelay release train once stable).
- Document compatibility matrix between OmniRelay transport releases and MeshKit modules in `docs/knowledge-base/meshkit/README.md` once the new repo exists.
- Update `docs/architecture/transport-layer-vision.md` and the knowledge base to reference the external repo URLs and package IDs.

## 6. Post-Extraction Cleanup
- Remove legacy directories (`src/OmniRelay/Core/Gossip`, etc.) from OmniRelay repo once package consumption is validated.
- Archive old WORK docs under `docs/project-board/history/` and replace them with pointers to MeshKit’s issue tracker/board.
- Update samples (e.g., `samples/ResourceLease.MeshDemo`) to consume MeshKit as a package rather than project references.
- Communicate the change to contributors (README, AGENTS.md startup prompt, knowledge base index) so future contributions target the correct repository.

Following this plan keeps OmniRelay transport-focused while giving MeshKit a clean runway to evolve independently once WORK-010..WORK-022 stabilize.
