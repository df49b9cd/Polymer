# WORK-022 – Samples & Docs Alignment

## Goal
Update samples and documentation to reflect the OmniRelay/MeshKit BRD/SRS, covering deployment modes, control-plane roles, extension lifecycle, and security practices.

## Scope
- Samples for in-proc, sidecar, and headless edge OmniRelay; control-plane central/agent/bridge setups.
- End-to-end extension lifecycle examples (DSL, Wasm, native) with signed packages and rollout.
- Guides for identity bootstrap/rotation, capability negotiation, LKG behavior, and failover drills.
- Knowledge-base updates in `docs/knowledge-base` and architecture docs cross-links.

## Requirements
1. **Accuracy** – Matches current schemas, commands, and behaviors; validated via runnable scripts/tests.
2. **Performance/Security notes** – Call out AOT constraints, watchdog defaults, signed artifacts, least-privilege deployment steps.
3. **Discoverability** – Clear entry points per persona (operator, platform, service dev).

## Deliverables
- Updated docs in `docs/architecture`, `docs/knowledge-base`, and `samples/` with runnable scripts.
- Diagrams for control/data flows and extension pipeline.

## Acceptance Criteria
- Samples execute in CI or nightly smoke; outputs documented.
- Docs reference latest CLI commands and schemas; outdated references removed.

## Testing Strategy
- Scripted sample runs; link checks; doc tests where applicable.

## References
- `docs/architecture/OmniRelay.BRD.md`
- `docs/architecture/MeshKit.BRD.md`

## Status
Needs re-scope (post-BRD alignment).
