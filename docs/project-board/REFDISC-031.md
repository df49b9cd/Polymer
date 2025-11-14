# REFDISC-031 - Shared Test Harness Toolkit

## Goal
Refactor reusable cluster orchestration, fault injection, and assertion helpers from the feature/hyperscale test suites into a shared testing toolkit so new services can adopt the same deterministic test infrastructure.

## Scope
- Extract cluster builder utilities, port allocators, chaos injectors, and common assertions from `OmniRelay.FeatureTests` and `OmniRelay.HyperscaleFeatureTests`.
- Package them into a library consumable by other test projects and future control-plane components.
- Provide documentation/examples for writing tests using the toolkit.
- Ensure toolkit remains test-only (no production dependencies).

## Requirements
1. **Deterministic orchestration** - Cluster builder must provide reproducible node setups, time providers, and configuration snapshots.
2. **Fault injection** - Support standardized injections (latency, packet loss, node restart) with consistent APIs.
3. **Assertions** - Include high-level assertions for gossip convergence, leadership stability, and transport health.
4. **Extensibility** - Allow new test suites to plug in custom scenarios without modifying core toolkit.
5. **Isolation** - Toolkit should not pull in production-only dependencies; keep it focused on tests.

## Deliverables
- Test harness toolkit (`OmniRelay.Testing.Infrastructure`) with orchestration + fault injection helpers.
- Feature/hyperscale test suites refactored to use the toolkit.
- Documentation (README + samples) describing how to write new tests using the toolkit.

## Acceptance Criteria
- Existing tests continue to pass using the toolkit with no behavioral regression.
- New test projects can compose clusters/faults with minimal boilerplate.
- Toolkit remains stable under CI load and supports both local + CI environments.
- Documentation provides clear guidance for contributors.

- Native AOT gate: Publish with /p:PublishAot=true and treat trimming warnings as errors per REFDISC-034..037.

## Testing Strategy
All test tiers must run against native AOT artifacts per REFDISC-034..037.


### Unit tests
- Validate deterministic cluster configuration generation (same input -> same output).
- Test fault injector scheduling/execution semantics.
- Ensure assertion helpers detect expected conditions and produce helpful failure messages.

### Integration tests
- Run sample test scenarios using the toolkit to verify cluster spin-up, fault injection, and convergence checks.
- Confirm toolkit works across OS/environment targets used in CI.
- Validate cleanup/teardown logic to prevent resource leaks.

### Feature tests
- Apply the toolkit within OmniRelay.FeatureTests, ensuring readability improves and results remain stable.
- Encourage new feature tests to use the toolkit for scenarios previously hard to automate.

### Hyperscale Feature Tests
- In OmniRelay.HyperscaleFeatureTests, verify the toolkit handles large node counts and intensive chaos sequences without degrading reliability.
- Measure orchestration overhead to ensure it scales acceptably.

## Implementation status
- `tests/OmniRelay.FeatureTests/Fixtures/*.cs` now provide the shared harness: `FeatureTestApplication` spins up dispatcher hosts via `Host.CreateApplicationBuilder`, allocates unique ports through `TestPortAllocator`, binds TLS with `TestCertificateFactory`, and exposes `FeatureTestContainers` so suites can opt into Postgres/EventStore/object storage/NATS containers on demand.
- Hyperscale orchestration lives under `tests/OmniRelay.HyperscaleFeatureTests/Infrastructure`, where `HyperscaleLeadershipCluster`, `LaggyLeadershipStore`, and `HyperscaleTestEnvironment` manage dozens of dispatcher roles, deterministic seeds, and chaos injections while reusing the same toolkit primitives (port allocator, TLS, configuration snapshots).
- Common helpers sit in `tests/TestSupport` (diagnostic TLS certs, HTTP/3 facts, JSON contexts, and TestRuntimeConfigurator) and are referenced by every suite plus the documentation in `tests/OmniRelay.FeatureTests/README.md` / `tests/OmniRelay.HyperscaleFeatureTests/README.md`, so new control-plane services can adopt the toolkit without rewriting scaffolding.

## References
- `tests/OmniRelay.FeatureTests` and `tests/OmniRelay.HyperscaleFeatureTests`.
- REFDISC-034..037 - AOT readiness baseline and CI gating.
