# WORK-002 – OmniRelay AOT Compliance Baseline

## Goal
Ensure OmniRelay transport/runtime assemblies (dispatcher, transport builders, middleware, diagnostics runtimes) compile cleanly with `PublishAot`, avoid trimming blockers, and provide guidance for future contributions.

## Scope
- Inventory AOT blockers across `src/OmniRelay` (reflection, dynamic assemblies, non-trimmable dependencies, expression trees) and remediate via source generators or annotations.
- Replace runtime reflection/Emit usage with compile-time alternatives or `DynamicDependency` hints.
- Guarantee transport hosts, middleware, diagnostics, CLI bootstrap components avoid unsupported APIs.
- Document AOT-safe coding patterns and add analyzers/build scripts that fail on violations.

## Requirements
1. **Compile cleanly** – `dotnet publish -r linux-x64 -c Release /p:PublishAot=true OmniRelay.slnx` succeeds without warnings suppressed.
2. **Trimming safety** – All OmniRelay assemblies enable trimming and annotate required members.
3. **Serializer coverage** – Use source-generated serializers for control-plane payloads and CLI contexts.
4. **Analyzer/CI** – Add analyzers/build steps that catch disallowed APIs and run during CI.
5. **Docs** – Provide contributor guidance on writing trimming/AOT-safe code.

## Deliverables
- Remediated code, source generators, annotations, analyzers/build tooling, documentation.

## Acceptance Criteria
- OmniRelay publishes native AOT binaries with zero warnings and passes smoke/integration tests.
- Reflection usage is either removed or explicitly annotated with justification.
- Documentation reviewed/approved by runtime owners.

## Testing Strategy
- Unit: tests for new source generators/compile-time bindings.
- Integration: native AOT builds running transport/integration suites.
- Feature/Hyperscale: run feature/hyperscale tests against AOT dispatcher verifying behavior.

## References
- `docs/architecture/transport-layer-vision.md`
- `docs/project-board/transport-layer-plan.md`

## Status & Validation
- Completed on November 19, 2025. Native AOT publish runs with trimming-safe routing/serialization for diagnostics, shard controls, bootstrap, and gossip wiring; reflection binding removed or suppressed for non-AOT-only surfaces.
- Validation commands:
  - `dotnet build OmniRelay.slnx`
  - `bash ./eng/run-aot-publish.sh` (linux-x64 Release)

## Notes
- Diagnostics, probe, and gossip endpoints are marked optional for AOT; suppressions document their exclusion from native images. Symbol stripping step requires `llvm-objcopy/objcopy` availability on host; set `StripSymbols=false` if toolchain is absent.
