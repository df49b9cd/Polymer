# WORK-003B – Proxy-Wasm Runtime Selection & ABI Support

## Goal
Support Proxy-Wasm ABI 0.2.x with selectable runtime (V8 default; Wasmtime/WAMR if built), including capability signaling.

## Scope
- Runtime selection config/build flags; capability advertisement of available runtimes.
- Load/instantiate Wasm modules with signature verification (reusing registry manifest data).
- Basic watchdogs for CPU/memory per VM.

## Acceptance Criteria
- Wasm module loads and runs sample filters in all modes.
- Capability flags reflect runtime availability; incompatible modules rejected.
- Watchdog breach enforces configured policy.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
