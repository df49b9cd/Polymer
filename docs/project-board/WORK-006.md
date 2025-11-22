# WORK-006 – Control Protocol (xDS-like) & Capability Negotiation

## Goal
Define and ship the versioned control-plane protocol between MeshKit and OmniRelay (central → agent → data plane) with capability negotiation, deltas/snapshots, and epoch handling.

## Scope
- Protobuf schemas for routes, clusters, policies, extensions, capability sets, and epochs/terms.
- Watch streams (gRPC) supporting deltas and full snapshots with resume tokens and backoff guidance.
- Capability negotiation: nodes advertise supported runtimes/features; server tailors payloads; down-level handling defined.
- Error semantics and LKG signaling.

## Requirements
1. **Versioning** – Backward-compatible schema evolution with explicit deprecation windows.
2. **Reliability** – Idempotent apply; monotonic epochs; resume from tokens after disconnect.
3. **Security** – mTLS on control channels; signed payload validation; RBAC on mutation APIs.
4. **Observability** – Metrics/logs for stream state, lag, rejections, capability mismatches.

## Deliverables
- Protobuf definitions and generated clients/servers.
- Reference server/client in MeshKit/OmniRelay with feature flags for capabilities.
- Docs for operators and developers on negotiation and error handling.

## Acceptance Criteria
- OmniRelay can subscribe and apply deltas/snapshots; mismatched capabilities are rejected with actionable errors.
- Resume tokens and epochs verified via integration tests.
- Control streams run over mTLS with certs from MeshKit identity.

## Testing Strategy
- Contract tests for schema compatibility and negotiation.
- Integration tests with drop/reconnect, version skew, and capability mismatches.

## References
- `docs/architecture/MeshKit.BRD.md`
- `docs/architecture/MeshKit.SRS.md`

## Status
Needs re-scope (post-BRD alignment).
