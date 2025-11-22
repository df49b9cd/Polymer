# WORK-006 – Epic: Control Protocol & Capability Negotiation

Split into iteration-sized stories (A–D).

## Child Stories
- **WORK-006A** – Protobuf schemas & versioning policy
- **WORK-006B** – Watch streams (deltas/snapshots) with resume/backoff
- **WORK-006C** – Capability negotiation handshake
- **WORK-006D** – Error/observability semantics

## Definition of Done (epic)
- Protocol implemented with negotiation, retries, and observability; used by MeshKit ↔ OmniRelay paths over mTLS.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
