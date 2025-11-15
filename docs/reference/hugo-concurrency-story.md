# Hugo Concurrency & Result Primitives Story

## Goal
- Evolve Hugo’s concurrency and Result pipeline primitives so they remain the foundation for hyperscale workflows (TaskQueues, Select loops, deterministic retries) across OmniRelay and other hosts.

## Scope
- Applies to packages under `src/Hugo.*` (TaskQueues, concurrency helpers, Result pipelines).
- Includes Select/Channel utilities, SafeTaskQueue ergonomics, ValueTask-first combinators, retry/compensation policies, and deterministic coordination glue.
- Excludes transport/middleware or control-plane orchestration (covered by OmniRelay/MeshKit stories).

## Requirements
1. Ensure every async combinator has both `Task` and `ValueTask` variants (`docs/reference/result-pipelines.md:60-118`) so high-volume workflows avoid allocations.
2. Maintain SafeTaskQueue APIs (`docs/reference/concurrency-primitives.md:331-360`) with extensibility hooks for backpressure monitors (Story 1) and replication (Story 2).
3. Provide deterministic coordination primitives (`docs/reference/deterministic-coordination.md`) with serializer context helpers and effect stores that integrate with replication sinks.
4. Keep error/result propagation consistent via `Result<T>` + `Error` types; update `OmniRelay.Errors` adapters when semantics change (`src/OmniRelay/Errors/OmniRelayErrors.cs`).
5. Continue shipping Go-style concurrency APIs (`docs/reference/hugo-api-reference.md:80-140`) aligned with .NET 10 TimeProvider guidance and instrumentation.
6. Document best practices for when to select `Task` vs `ValueTask`, `IDisposable` vs `IAsyncDisposable` (see earlier Q&A).
7. Backwards compatibility: maintain binary compatibility within major version; add analyzer warnings when new APIs supersede older ones.

## Deliverables
- Updated Hugo packages implementing new ValueTask combinators, queue helpers, deterministic utilities.
- Documentation refresh covering concurrency primitives, result pipelines, and patterns (docs + samples).
- Analyzer or Roslyn-based guidance encouraging new APIs.
- Migration notes for OmniRelay and other adopters.

## Acceptance Criteria
1. All concurrency/result APIs expose ValueTask-friendly overloads with tests proving allocation patterns.
2. SafeTaskQueue extensions support instrumentation hooks used by backpressure/replication packages.
3. Deterministic effect stores remain deterministic under restarts; serializer context resolution works across hosting models.
4. Documentation examples compile and reflect recommended usage.
5. OmniRelay builds/tests succeed against updated Hugo packages without code changes other than planned migrations.

## References
- `docs/reference/concurrency-primitives.md`
- `docs/reference/result-pipelines.md`
- `docs/reference/deterministic-coordination.md`
- `docs/reference/hugo-api-reference.md`
- `src/OmniRelay/Errors/OmniRelayErrors.cs`

## Testing Strategy
- Maintain existing Hugo unit suites; add coverage for new overloads and deterministic helpers.
- Stress tests for SafeTaskQueue operations under concurrent load.
- Integration tests verifying OmniRelay dispatcher scenarios using latest Hugo packages.
- Feature tests via samples (e.g., `samples/Quickstart.Server`) ensuring end-to-end behaviour.

### Unit Test Scenarios
- Validate every `Functional.*Async` helper has `*ValueTaskAsync` counterpart.
- Ensure Select fan-in utilities honor cancellation and deadlines with new hooks.
- Deterministic effect store serialization/deserialization round-trips.
- SafeTaskQueue wrapper metrics/backpressure signals integrate without thread-safety issues.

### Integration Test Scenarios
- OmniRelay dispatcher invoking Hugo pipelines across unary, streaming, and resource-lease flows.
- SafeTaskQueue stress harness verifying fairness and throughput with new monitors disabled/enabled.
- Deterministic coordinator replaying side effects in ASP.NET Core host restarts.

### Feature Test Scenarios
- Samples using `TaskQueueBackpressureMonitor` + ValueTask combinators to process workloads.
- Running `dotnet test tests/OmniRelay.Core.UnitTests/OmniRelay.Core.UnitTests.csproj` against upgraded Hugo bits to confirm compatibility.
- Benchmarks comparing Task vs ValueTask throughput to validate documented guidance.
