# WORK-022 – Epic: Samples & Docs Alignment

Split into iteration-sized stories (A–C).

## Child Stories
- **WORK-022A** – In-proc vs sidecar vs edge samples
- **WORK-022B** – Extension lifecycle samples (DSL/Wasm/Native)
- **WORK-022C** – Identity/capability/failover docs & diagrams

## Definition of Done (epic)
- Samples/docs match current schemas and modes; runnable scripts validate key paths.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
