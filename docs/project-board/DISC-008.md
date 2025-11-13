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

## Testing Strategy

### Unit tests
- Cover validator logic for cordon/drain/promote requests, ensuring preconditions (e.g., cannot demote the last active cluster) throw consistent problem-detail payloads.
- Test optimistic concurrency helpers to prove ETag mismatches reject stale updates while idempotent retries succeed.
- Verify audit log builders capture actor, reason, ticket ID, and outcome for every code path, including failures.

### Integration tests
- Run REST + gRPC mutation flows against a seeded registry, asserting RBAC scopes (`mesh.operate`, `mesh.admin`), transactionality, and audit trail persistence.
- Exercise CLI commands in record mode (with confirmation prompts) to validate serialization, error surfaces, and logging hooks.
- Simulate concurrent mutations (e.g., two drains) to ensure locking guarantees and descriptive conflict responses.

### Feature tests

#### OmniRelay.FeatureTests
- Execute a scripted operations scenario that cordons a node, drains workloads, promotes a backup cluster, and edits peer metadata, confirming `/control/*` endpoints and dashboards reflect each mutation.
- Perform a rollback drill where a drain is cancelled mid-flight, verifying the controller unwinds state, updates audit logs, and notifies subscribers through CLI and SSE feeds.

#### OmniRelay.HyperscaleFeatureTests
- Drive parallel mutations across dozens of peers/clusters to validate optimistic concurrency, audit throughput, and CLI ergonomics when many operators act at once.
- Stress destructive workflows (mass drains, config edits) with staged approvals to ensure guardrails, confirmation prompts, and telemetry remain responsive under scale.

## References
- `docs/architecture/service-discovery.md` – “Discoverable peer registry API”, “Required refactorings”, “Risks & mitigations”.
