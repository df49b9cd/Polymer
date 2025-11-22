# WORK-010 – Extension Registry & Admission (DSL/Wasm/Native)

## Goal
Provide a signed registry for DSL, Proxy-Wasm, and native extension bundles with admission checks, manifest metadata, and retrieval APIs for MeshKit and OmniRelay.

## Scope
- Manifest format: name, version, ABI/runtime requirements, hash, signature, dependencies, failure policy hints.
- Storage backend (OCI/HTTP) with integrity verification and caching strategy.
- Admission pipeline: signature verification, ABI/capability compatibility, dependency checks, policy validation.
- APIs/CLI to publish/list/promote/retire artifacts; audit logging.

## Requirements
1. **Security** – Signatures mandatory; root of trust pinned; optional timestamping; rejection on mismatch.
2. **Compatibility** – Enforce runtime/ABI requirements; integrate with capability negotiation (WORK-006/014).
3. **Rollout-ready metadata** – Include canary scopes, fail-open/closed defaults, and resource budgets for WORK-011.
4. **Caching** – Agents cache artifacts with hash checks; eviction policy defined.

## Deliverables
- Registry service/API, manifest schema, and storage implementation.
- CLI/SDK commands for publishing and querying artifacts.
- Docs on packaging, signing, and promotion workflows.

## Acceptance Criteria
- Artifact publish rejected on signature/ABI mismatch.
- OmniRelay can fetch and verify artifacts via agent/registry; hashes match manifest.
- Audit trail records who/when/what for publishes/promotes/retires.

## Testing Strategy
- Unit: manifest parsing, signature checks, capability validation.
- Integration: publish/fetch/retire flows; cache hit/miss; corruption detection.

## References
- `docs/architecture/MeshKit.BRD.md`
- `docs/architecture/OmniRelay.SRS.md`

## Status
Needs re-scope (post-BRD alignment).
