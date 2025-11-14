# REFDISC-019 - Shared State Store & Persistence Adapters

## Goal
Refactor deterministic state store and persistence adapters (SQLite, file, blob) into reusable components so control-plane services can persist data with the same transactional guarantees, retry policies, and telemetry as the dispatcher.

## Scope
- Extract the state store abstractions, retry logic, and provider implementations from dispatcher/samples.
- Provide adapters for SQLite/local file, cloud blobs, and extensibility hooks for additional storage backends.
- Integrate telemetry (latency, retries) and diagnostics endpoints for persistence health.
- Document configuration and usage patterns for both dispatcher and auxiliary services.

## Requirements
1. **Transactional semantics** - Preserve deterministic commit/rollback guarantees across adapters.
2. **Retry/backoff** - Include configurable retry policies for transient failures with observability on retry counts.
3. **Pluggable providers** - Allow new storage backends to be registered without modifying core abstractions.
4. **Telemetry** - Emit metrics/logs for read/write latency, errors, and retries.
5. **Configuration** - Support binding storage settings (connection strings, paths) through the shared configuration kit.

## Deliverables
- State store abstraction library (`OmniRelay.Persistence`) with built-in adapters.
- Dispatcher and sample refactor to consume the shared library.
- Control-plane services updated to use the adapters for their persistence needs.
- Documentation covering configuration, adapter selection, and telemetry.

## Acceptance Criteria
- Existing persistence behavior (transaction boundaries, retry policies) remains unchanged in dispatcher scenarios.
- Control-plane services can adopt the adapters without referencing dispatcher internals.
- Telemetry dashboards capture persistence metrics for all hosts using the library.
- Configuration errors produce consistent validation messages.
- Library introduces no dispatcher-only dependencies.

- Native AOT gate: Publish with /p:PublishAot=true and treat trimming warnings as errors per REFDISC-034..037.

## Testing Strategy
All test tiers must run against native AOT artifacts per REFDISC-034..037.


### Unit tests
- Cover transaction commit/rollback flows for each adapter.
- Test retry/backoff logic under simulated transient failures.
- Validate configuration binding and error messaging for invalid settings.

### Integration tests
- Run real storage operations (SQLite/file/blob) through the adapters and verify data consistency.
- Inject failures (file lock, network error) to ensure retries behave as expected.
- Expose diagnostics endpoints for persistence health and verify output.

### Feature tests
- In OmniRelay.FeatureTests, switch dispatcher/sample hosts to the shared adapters and verify workflows (lease persistence) behave identically.
- Exercise failover scenarios (storage path swap) to ensure adapters reconfigure cleanly.

### Hyperscale Feature Tests
- Under OmniRelay.HyperscaleFeatureTests, stress adapters with high write throughput to ensure transactional guarantees hold and telemetry remains stable.
- Simulate regional storage outages to confirm retries/backoff avoid cascading failures.

## Implementation status
- Shared deterministic state stores now ship in `src/OmniRelay/Dispatcher/FileSystemDeterministicStateStore.cs` and `src/OmniRelay.ResourceLeaseReplicator.Sqlite/SqliteDeterministicStateStore.cs`, giving control-plane services filesystem, SQLite, or in-memory persistence options that implement the same `IDeterministicStateStore` contract and JSON codecs as the dispatcher.
- `src/OmniRelay/Dispatcher/ResourceLeaseDeterministic.cs` wires those stores into `DeterministicResourceLeaseCoordinator`, letting the dispatcher, CLI, and the `samples/ResourceLease.MeshDemo` host capture replication effects with identical change/version gates while `SqliteResourceLeaseReplicator` / `ObjectStorageResourceLeaseReplicator` provide durable fan-out adapters.
- Determinism is enforced by `tests/OmniRelay.Dispatcher.UnitTests/FileSystemDeterministicStateStoreTests.cs`, `tests/OmniRelay.Dispatcher.UnitTests/SqliteDeterministicStateStoreTests.cs`, and `tests/OmniRelay.Dispatcher.UnitTests/ResourceLeaseReplicationTests.cs`, while `tests/OmniRelay.IntegrationTests/ResourceLeaseIntegrationTests.cs` runs the dispatcher, middleware, and replication sinks end-to-end under real HTTP metadata to ensure behavior stays unchanged for consumers.

## References
- Existing deterministic state store implementations in samples/dispatcher.
- REFDISC-034..037 - AOT readiness baseline and CI gating.
