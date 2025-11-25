# WORK-008 – Epic: MeshKit Local Agent

Split into iteration-sized stories (A–D).

## Child Stories
- **WORK-008A** – LKG cache & signature validation
- **WORK-008B** – Control stream client (resume/backoff) without election
- **WORK-008C** – Telemetry forwarding with buffering/backpressure
- **WORK-008D** – Resource/security hardening and footprint measurements

## Definition of Done (epic)
- Agent reliably caches/apply LKG, renews certs, forwards telemetry, and remains lightweight and non-authoritative.

## Status
Done — MeshAgent implemented with signed LKG cache, telemetry forwarder, hosted-service wiring (`AddMeshAgent`), control watch client reuse, certificate renewal scheduler, and leadership suppression. LKG persistence and telemetry hooks in place.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
