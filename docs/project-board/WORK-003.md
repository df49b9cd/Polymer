# WORK-003 – Epic: Extension Hosts (DSL, Proxy-Wasm, Native)

Split into iteration-sized stories (A–D).

## Child Stories
- **WORK-003A** – DSL host MVP (signed packages, opcode allowlist)
- **WORK-003B** – Proxy-Wasm runtime selection & ABI support
- **WORK-003C** – Native plugin ABI + watchdog policies
- **WORK-003D** – Extension telemetry & failure policy wiring

## Definition of Done (epic)
- All extension types load safely with quotas, telemetry, and capability signals across deployment modes.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
