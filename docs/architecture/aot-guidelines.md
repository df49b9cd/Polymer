# OmniRelay Native AOT Guidelines

OmniRelay targets cloud-native, hyperscale environments where footprint, cold-start, and determinism are key. Native AOT (ahead-of-time compilation) is therefore the default delivery model for every runtime component, control-plane service, and CLI/tool.

This document captures the baseline expectations introduced by `WORK-002`:

1. **Native builds must succeed** – Every shipping project must publish with `dotnet publish /p:PublishAot=true` (see scripts below) with zero trimming warnings or suppressed diagnostics.
2. **Trimming-safe code** – Source generators, explicit `DynamicallyAccessedMembers` annotations, or strongly typed registries must replace reflection-heavy patterns, `Activator.CreateInstance`, or runtime code generation whenever possible.
3. **Shared build scripts** – Use the `eng/run-aot-publish.sh` helper (or PowerShell equivalent) to produce validated AOT binaries locally and in CI; do not invent per-project workflows.
4. **Telemetry + diagnostics** – All runtime diagnostics pipelines must remain functional in AOT builds; any diagnostic scenario unavailable in AOT must be documented with a mitigation plan.
5. **Contributor contract** – New features cannot merge unless their acceptance criteria explicitly states that native AOT publish and testing have passed (see `docs/project-board/README.md`).

---

## Baseline Build Commands

Use the shared script (`eng/run-aot-publish.sh`) to create native artifacts. It defaults to `linux-x64` but accepts any RID that .NET 10 supports:

```bash
# Build dispatcher + CLI native binaries (linux-x64)
./eng/run-aot-publish.sh

# Build for linux-arm64 in Release
./eng/run-aot-publish.sh linux-arm64 Release
```

The script currently targets:

| Project | Output folder |
| ------- | ------------- |
| `src/OmniRelay/OmniRelay.csproj` | `artifacts/aot/<rid>/dispatcher` |
| `src/OmniRelay.Cli/OmniRelay.Cli.csproj` | `artifacts/aot/<rid>/cli` |

> Extend the `projects` array in the script when new control-plane binaries are added.

---

## Reflection & Source Generation

- Prefer source-generated serializers (`JsonSerializerContext`, protobuf code-gen, gRPC service descriptors) over runtime reflection.
- When DI needs to activate types discovered by attributes, use generated registries instead of `Assembly.GetTypes()`.
- If reflection is unavoidable, annotate the target members with `DynamicallyAccessedMembers` and add unit tests that exercise the trimmed scenario.
- Avoid `System.Reflection.Emit`, expression tree compilation, and runtime `ILGenerator` usage entirely—they are not supported in Native AOT.

---

## Diagnostics & Observability

- Validate that all metrics/logging/tracing providers work under AOT builds. Register meters/activity sources statically and avoid lazy reflection to discover instrumentation.
- Ensure `/omnirelay/control/*` endpoints are reachable in AOT artifacts and continue to emit JSON payloads that match their JIT counterparts.
- Include at least one AOT smoke test per story (unit/integration/feature/hyperscale) as described in `docs/project-board/README.md`.

---

## Reporting & Known Issues

- When an AOT blocker is discovered (third-party dependency, runtime bug), document it in the story and add it to a new section in `docs/architecture/service-discovery.md`.
- If a blocker cannot be resolved immediately, add a CI guard that fails fast with a clear message so contributors know why the gate is red.

---

## Next Steps

- `WORK-003` extends these rules to every shared control-plane library.
- `WORK-004` delivers native AOT tooling packages.
- `WORK-005` wires these checks into CI so PRs cannot regress the baseline.

Refer to this document whenever you add dependencies, introduce new runtime features, or review a PR for AOT-readiness.
