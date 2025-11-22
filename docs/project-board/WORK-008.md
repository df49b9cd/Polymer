# WORK-008 – MeshKit Local Agent (LKG Cache & Telemetry Forwarder)

## Goal
Deliver a lightweight MeshKit agent that subscribes to the control domain, caches last-known-good (LKG) config/artifacts, renews certs, and forwards telemetry—without participating in leader election.

## Scope
- Control-plane watch client with resume/backoff and LKG persistence.
- Cert renewal client integrating with WORK-007.
- Telemetry forwarding (OTLP) with buffering/backpressure; optional local sampling.
- Health reporting to central MeshKit (status, version, epoch, capability).

## Requirements
1. **Non-authoritative** – Agent never elects leaders; trusts domain epochs/terms; rejects conflicting payloads.
2. **Resilience** – Uses LKG when central unreachable; configurable cache TTL and safety checks.
3. **Resource bounds** – Small memory/CPU footprint; bounded queues for telemetry and config.
4. **Security** – Verifies signatures/certs for config/artifacts; runs with least-privilege FS access.

## Deliverables
- Agent service/library with install/run instructions.
- Persistence for LKG (on-disk) with hash/signature checks.
- Metrics/logs for cache hits/misses, resume attempts, and forwarding lag.

## Acceptance Criteria
- During partition, OmniRelay continues with LKG; agent reports degraded state; resync on recovery.
- Telemetry buffering prevents data loss within configured bounds; drop policies observable.
- AOT publish and smoke tests pass for agent binary.

## Testing Strategy
- Integration: disconnect/reconnect scenarios; LKG apply; resume tokens.
- Perf: measure agent resource footprint under load.
- Security: signature/cert validation tests and permission checks.

## References
- `docs/architecture/MeshKit.SRS.md`
- `docs/architecture/MeshKit.BRD.md`

## Status
Needs re-scope (post-BRD alignment).
