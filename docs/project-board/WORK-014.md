# WORK-014 – Capability Down-Leveling & Schema Evolution

## Goal
Allow MeshKit to tailor control-plane payloads to OmniRelay/agent capabilities, manage schema evolution, and keep mixed-version fleets healthy.

## Scope
- Capability advertisement from OmniRelay/agents (runtimes, limits, features, build epoch).
- Server-side tailoring of payloads (e.g., omit Wasm if unsupported, choose DSL version).
- Compatibility matrix and deprecation policy with grace periods.
- Validation tooling to detect incompatible configs before rollout.

## Requirements
1. **Negotiation** – Mandatory capability exchange during bootstrap; refusal with clear errors when incompatible.
2. **Policy** – Configs tagged with required capabilities; rollout blocked if unmet.
3. **Evolution** – Schema versioning with automated compatibility tests; doc deprecation timelines.
4. **Visibility** – Metrics/logs for down-level decisions and rejected payloads.

## Deliverables
- Capability model definitions and negotiation protocol changes (client + server).
- Compatibility checker CLI/CI task.
- Docs for operators on capability flags and support windows.

## Acceptance Criteria
- Mixed fleets receive tailored payloads; incompatible nodes reject gracefully and fall back to LKG.
- CI compatibility checker flags configs requiring unavailable capabilities.
- Deprecation timelines published and enforced.

## Testing Strategy
- Integration: mixed capability clusters; ensure correct tailoring/rejection.
- Contract tests for schema evolution and compatibility matrix.

## References
- `docs/architecture/OmniRelay.SRS.md`
- `docs/architecture/MeshKit.SRS.md`

## Status
Needs re-scope (post-BRD alignment).
