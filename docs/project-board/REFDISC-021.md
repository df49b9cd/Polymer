# REFDISC-021 - Resource Lease Replication Framework

## Goal
Refactor resource lease replication/logging components into a shared framework so dispatcher and control-plane services can emit, persist, and replay replication events with consistent serialization and telemetry.

## Scope
- Extract replication sinks, loggers, and transport adapters from the existing samples/dispatcher code.
- Provide an event schema, serialization helpers, and pluggable sinks (in-memory, file, message bus).
- Integrate with the shared persistence adapters where applicable.
- Document replication workflows and extension points.

## Requirements
1. **Schema consistency** - Define versioned replication event schemas compatible with current implementations.
2. **Reliability** - Support at-least-once delivery semantics with idempotent sinks/replay mechanisms.
3. **Pluggable sinks** - Allow registering multiple sinks with independent failure handling.
4. **Telemetry** - Emit metrics/logs for replication throughput, lag, and failures.
5. **Configuration** - Bind replication settings (enabled sinks, batch sizes) via the shared configuration kit.

## Deliverables
- Replication framework library (`OmniRelay.ControlPlane.Replication`).
- Dispatcher/sample refactor to use the framework.
- Control-plane services (e.g., diagnostics) updated to publish/consume replication events.
- Documentation covering schemas, sink configuration, and monitoring.

## Acceptance Criteria
- Replication behavior in existing samples (mesh demo) remains unchanged after adopting the framework.
- New sinks can be registered without modifying core dispatcher code.
- Telemetry dashboards show replication metrics for all hosts using the framework.
- Schema evolution tests confirm compatibility across versions.
- Framework introduces no dispatcher-specific dependencies.

- Native AOT gate: Publish with /p:PublishAot=true and treat trimming warnings as errors per REFDISC-034..037.

## Testing Strategy
All test tiers must run against native AOT artifacts per REFDISC-034..037.


### Unit tests
- Serialize/deserialize replication events and validate version handling.
- Test sink behavior (in-memory, file) for idempotency and error handling.
- Validate configuration binding for sink settings.

### Integration tests
- Wire the framework into a sample service, emit replication events, and verify sinks receive them reliably.
- Simulate sink failures to ensure retries/backoff prevent data loss.
- Replay events from logs to confirm determinism.

### Feature tests
- In OmniRelay.FeatureTests, enable replication across dispatcher/control-plane services and ensure operator tooling observes events as before.
- Validate replication metrics/logs appear in observability dashboards.

### Hyperscale Feature Tests
- Under OmniRelay.HyperscaleFeatureTests, drive high event volumes to ensure sinks keep up and backpressure mechanisms engage when needed.
- Test schema rollover scenarios to ensure mixed-version nodes handle events safely.

## Implementation status
- The replication schema, sequencing logic, and sink abstractions are implemented in `src/OmniRelay/Dispatcher/ResourceLeaseReplication.cs`, complete with immutable `ResourceLeaseReplicationEvent` records, checkpointing sinks, and `ShardedResourceLeaseReplicator`/`CompositeResourceLeaseReplicator` helpers so dispatcher- and control-plane-owned queues emit the exact same payloads.
- Dedicated adapters ship via `src/OmniRelay.ResourceLeaseReplicator.Sqlite`, `.ObjectStorage`, and `.Grpc`, each persisting events before invoking downstream sinks. The gRPC adapter shares `ResourceLeaseReplication.proto` contracts and automatically maps to `ResourceLeaseJsonContext`, while the SQLite/object-store variants expose deterministic fan-out and resume logic.
- Reliability coverage includes `tests/OmniRelay.Dispatcher.UnitTests/ResourceLeaseReplicationTests.cs` + `ResourceLeaseShardingReplicatorTests.cs`, the full dispatcher flow in `tests/OmniRelay.IntegrationTests/ResourceLeaseIntegrationTests.cs`, and the `samples/ResourceLease.MeshDemo` host, which wires the framework into TaskQueue workloads and validates telemetry/backpressure hooks in a real control-plane deployment.

## References
- Resource lease replication components in `samples/ResourceLease.MeshDemo` and core dispatcher plans.
- REFDISC-034..037 - AOT readiness baseline and CI gating.
