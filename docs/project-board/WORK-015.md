# WORK-015 – Routing & Policy Engine with Multi-Version Canary

## Goal
Compute and distribute routing/policy bundles (routes, clusters, retries/timeouts, authz) with support for staged multi-version rollout and verification before global apply.

## Scope
- Policy computation from service inventory and operator inputs; output route/cluster/authz bundles with epochs.
- Multi-version bundles to support canary/blue-green (ties into WORK-011).
- Simulation/diff tools for operators (pre-flight checks) and LKG fallbacks.
- Per-route metadata for extension bindings and failure policies.

## Requirements
1. **Determinism** – Same inputs produce same bundle; include hash/version metadata.
2. **Validation** – Static checks (syntax, references, capability fit) before publish.
3. **Safety** – Staged apply; rollback on regression signals; LKG preserved.
4. **Performance** – Incremental recompute and diff; avoid full recompute when unnecessary.

## Deliverables
- Policy engine service/library; protobuf/OpenAPI outputs.
- CLI/UX for diff/simulate/apply; integration with rollout manager.
- Tests and docs for policy fields and extension binding semantics.

## Acceptance Criteria
- Bundles generated with hashes/epochs; applied via control protocol; staged rollout works in tests.
- Simulation catches bad references or capability mismatches pre-publish.
- LKG fallback verified in failure scenarios.

## Testing Strategy
- Unit: policy resolution, diffing, validation rules.
- Integration: end-to-end bundle publish, canary, rollback; mixed capability nodes.

## References
- `docs/architecture/MeshKit.SRS.md`
- `docs/architecture/OmniRelay.SRS.md`

## Status
Needs re-scope (post-BRD alignment).
