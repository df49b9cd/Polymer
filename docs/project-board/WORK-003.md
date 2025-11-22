# WORK-003 – Extension Hosts (DSL, Proxy-Wasm, Native) with Watchdogs

## Goal
Provide secure, performant extension hosting for DSL, Proxy-Wasm, and native plugins with resource guards, failure policies, and capability signaling.

## Scope
- DSL interpreter (AOT-compiled) with opcode allowlist, quotas, and signature validation.
- Proxy-Wasm host supporting selectable runtime (V8 default; Wasmtime/WAMR if built) and ABI 0.2.x.
- Native plugin loader using `NativeLibrary.Load` + `UnmanagedCallersOnly` ABI handshake.
- Watchdogs for CPU/heap per extension, fail-open/closed/reload policies, and telemetry.
- Capability flags exposed to MeshKit (runtime availability, limits, ABI versions).

## Requirements
1. **Safety** – Verify signatures before load; sandbox memory/CPU; terminate on limit breach.
2. **Observability** – Metrics/logs for load, instantiation, faults, watchdog hits, and boundary latency.
3. **Parity** – Same extension behavior across deployment modes; VM per worker unless singleton service allowed.
4. **Failure policy** – Configurable per extension (fail-open/closed/reload); defaults documented.
5. **Capability negotiation** – Advertise supported runtimes/ABI to MeshKit; refuse incompatible payloads.

## Deliverables
- Extension host implementations with policy hooks and watchdogs.
- Telemetry surfaces and admin views for extension state.
- Docs describing packaging, signature expectations, and failure policies.

## Acceptance Criteria
- Invalid or unsigned packages are rejected before load.
- Watchdog breach results in configured policy action and telemetry.
- Proxy-Wasm/DSL/native samples run across modes; capability flags accurate.
- AOT publish/test suites pass with extension hosts enabled.

## Testing Strategy
- Unit: manifest validation, watchdog triggers, capability negotiation paths.
- Integration: run sample DSL/Wasm/native plugins; verify policies and telemetry.
- Chaos: inject infinite loop/out-of-memory to confirm watchdog handling.

## References
- `docs/architecture/OmniRelay.BRD.md`
- `docs/architecture/OmniRelay.SRS.md`

## Status
Needs re-scope (post-BRD alignment).
