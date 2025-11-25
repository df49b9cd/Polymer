# WORK-003 – Epic: Extension Hosts (DSL, Proxy-Wasm, Native)

Split into iteration-sized stories (A–D).

## Status
Done (Phase 1 complete; Phase 2 deferred)

## Child Stories
- **WORK-003A** – DSL host MVP (signed packages, opcode allowlist)
- **WORK-003B** – Proxy-Wasm runtime selection & ABI support _(Deferred)_
- **WORK-003C** – Native plugin ABI + watchdog policies _(Deferred)_
- **WORK-003D** – Extension telemetry & failure policy wiring (covers DSL now; will extend to Wasm/native when resumed)

## Definition of Done (epic)
- Phase 1 (complete): DSL extensions load safely with signatures, opcode allowlist, quotas, telemetry, failure policies, and diagnostics endpoints across deployment modes.
- Phase 2 (deferred): Proxy-Wasm and native plugin hosts added with equivalent guarantees and telemetry. Re-activate WORK-003B/003C when scheduled.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.

## Validation & CI
- DSL host coverage lives in `tests/OmniRelay.Core.UnitTests/Extensions/DslExtensionHostTests.cs` (signatures, opcode allowlist, quotas, failure policies).
- CI entrypoints: `dotnet test tests/OmniRelay.Core.UnitTests/OmniRelay.Core.UnitTests.csproj` (invoked via `./eng/run-ci-gate.sh` and `./eng/run-ci.sh`).
