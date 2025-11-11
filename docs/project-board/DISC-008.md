# DISC-008 – Registry Mutation APIs

## Goal
Enable operators and automation to modify peer and cluster state via authenticated control-plane operations (cordon, drain, label, promote, config edit) with full auditing.

## Scope
- Add mutation verbs to control-plane service:
  - `POST /control/peers/{id}:cordon|uncordon`
  - `POST /control/peers/{id}:drain`
  - `PATCH /control/peers/{id}` for labels/metadata
  - `POST /control/clusters/{id}:promote|demote`
  - `PATCH /control/config` for approved settings
- Implement optimistic concurrency (ETag) and audit logging.
- Surface operations via CLI (`omnirelay mesh peers drain ...`, `... clusters promote ...`) with confirmation prompts.

## Requirements
1. **RBAC** – Require `mesh.operate` or `mesh.admin` scopes; double-confirm destructive actions.
2. **Workflow hooks** – Support optional change-ticket IDs and reason strings stored in audit log.
3. **Safety** – Validate preconditions (e.g., cannot demote last active cluster) and return actionable errors.
4. **Idempotency** – Mutations must be idempotent; repeated requests should not cause inconsistent state.
5. **Telemetry** – Log actor, action, target, outcome, and latency; emit metrics for operations/sec and failures.

## Deliverables
- API handlers, validation logic, and persistence updates.
- CLI commands with tests covering happy paths and validation failures.
- Audit log sink + viewer (initially CLI + log files).
- Documentation/runbooks for each operation.

## Acceptance Criteria
- Executing mutations updates registry state, enforces RBAC, and records audit entries.
- CLI workflows demonstrate cordon/drain/promote flows end-to-end in integration tests.
- Error handling covers invalid states with descriptive messages and HTTP status codes.

## References
- `docs/architecture/service-discovery.md` – “Discoverable peer registry API”, “Required refactorings”, “Risks & mitigations”.
