# WORK-009 – Epic: Bootstrap & Watch Harness

Split into iteration-sized stories (A–C).

## Child Stories
- **WORK-009A** – Deterministic startup pipeline (LKG → stage → activate) **[Done]**
- **WORK-009B** – Watch lifecycle (backoff/resume/state machine) **[Done]**
- **WORK-009C** – Validation hooks & observability **[Done]**

## Definition of Done (epic)
- Shared harness used by all hosts/roles; startup and watch flows observable and resilient.

## Completion Notes
- Introduced ControlPlane Agent harness: `WatchHarness`, `LkgCache`, `MeshAgent`, hosted service, validators/appliers.
- Control-plane watch protocol defined in `control_plane.proto` with client abstraction; agent uses backoff + resume and stages configs before activation.
- Validation/applier hooks pluggable via DI; telemetry forwarder emits lifecycle logs; LKG fallback retained.
- Added service collection extension for hosts to wire the harness; CA and watch services exposed from control plane for agents.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
