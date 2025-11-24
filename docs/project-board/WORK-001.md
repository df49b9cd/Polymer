# WORK-001 – Epic: OmniRelay Transport & Pipeline Parity

This epic is split into iteration-sized stories (A–C) to ensure each can complete in one sprint without breaking the system.

## Child Stories
- **WORK-001A** – Mode-config parity & capability flags
- **WORK-001B** – Admin/diagnostics parity across modes
- **WORK-001C** – Cross-mode feature/perf validation

## Definition of Done (epic)
- All child stories done; identical behavior documented across in-proc, sidecar, and edge modes with published SLOs and green test suites.

## Status
Done — Mode-aware config and capability flags shipped, admin/introspection parity in place, and cross-mode validation baseline established.

## SLOs & CI gates
- Perf budgets (current baseline): InProc p99 ≤ 5 ms, Sidecar p99 ≤ 7 ms, Edge p99 ≤ 12 ms for unary requests with default pipelines; update values after each perf sweep per `docs/knowledge-base/dotnet-performance-guidelines.md`.
- CI enforcement: `dotnet test tests/OmniRelay.Dispatcher.UnitTests` (parity + validation) and `./eng/run-aot-publish.sh linux-x64 Release` are required gates; both are invoked by `./eng/run-ci.sh`.
- Artifacts: perf and validation outputs are emitted to `tests/OmniRelay.Dispatcher.UnitTests/TestResults` and CI uploads for trend tracking.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
