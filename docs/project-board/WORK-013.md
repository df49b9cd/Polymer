# WORK-013 – Mesh Bridge / Federation Between Control Domains

## Goal
Enable controlled federation between MeshKit control domains (regions/tenants) via mesh bridges that export selected routes/policies/identities without merging consensus rings.

## Scope
- Bridge role that subscribes to source domain, filters/rewrites allowed state, and republishes to target domain with new epochs.
- Export allowlists/denylists for services, routes, identities, and extensions.
- Identity mediation (trust bundle translation) and optional namespace rewriting.
- Queue/replay of deltas during partition with ordering guarantees.

## Requirements
1. **Isolation** – Domains keep separate leader elections; bridge never participates in either consensus ring.
2. **Policy Control** – Explicit export policies with audit; default deny.
3. **Consistency** – Ordered replay with epoch translation; duplicate suppression.
4. **Security** – mTLS on both sides; signature verification; cross-domain trust scoped per export policy.

## Deliverables
- Bridge service implementation and configuration model.
- Export policy definitions and validation tooling.
- Monitoring for replay lag, export errors, and trust failures.

## Acceptance Criteria
- Bridged routes/policies appear in target domain with translated epochs and correct scoping.
- Partitions lead to queued deltas; replay on recovery without divergence.
- Unauthorized exports are blocked and logged.

## Testing Strategy
- Integration: dual-domain harness with exports; partition/rejoin; trust failures.
- Perf: measure replay lag under sustained updates.

## References
- `docs/architecture/MeshKit.BRD.md`
- `docs/architecture/MeshKit.SRS.md`

## Status
Needs re-scope (post-BRD alignment).
