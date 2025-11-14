# REFDISC-016 - Registry Model & Serialization Library

## Goal
Break out the dispatcher’s registry data models, codecs, and serialization helpers into a standalone library so service-discovery components (registry APIs, CLI, tooling) can share schemas without duplicating implementations.

## Scope
- Isolate registry entities (shards, services, endpoints), Protobuf/JSON contracts, and serialization logic from dispatcher internals.
- Provide strongly typed builders/parsers plus versioning utilities for schema evolution.
- Ensure codecs are compatible with existing dispatcher RPC payloads and can be consumed by external tools.
- Document schema usage and versioning strategy for contributors.

## Requirements
1. **Schema parity** - Library must encode/decode registry payloads exactly as dispatcher currently does, including backward/forward compatibility handling.
2. **Versioning** - Provide explicit schema version constants and helpers to negotiate compatibility between producers/consumers.
3. **Extensibility** - Allow additional metadata fields via annotations/options without breaking existing consumers.
4. **Validation** - Include validators for required fields, constraints (e.g., shard ranges), and meaningful error messages.
5. **Integration** - Library should be consumable by .NET services and CLI tooling without requiring dispatcher dependencies.

## Deliverables
- Registry model/serialization library under `OmniRelay.Registry.Abstractions`.
- Dispatcher updates to reference the library instead of local models.
- Control-plane services and CLI updated to consume the shared models/codecs.
- Documentation of schema definitions, versioning policy, and migration guidelines.

## Acceptance Criteria
- Registry payloads produced/consumed by dispatcher remain byte-for-byte compatible post-migration.
- CLI/tooling successfully serialize/deserialize registry data using the shared library.
- Schema version negotiation works as documented, with tests covering mixed-version scenarios.
- Validation errors surface consistent messages referencing schema fields.
- Library contains no dispatcher runtime dependencies.

- Native AOT gate: Publish with /p:PublishAot=true and treat trimming warnings as errors per REFDISC-034..037.

## Testing Strategy
All test tiers must run against native AOT artifacts per REFDISC-034..037.


### Unit tests
- Serialize/deserialize all registry entities, ensuring round-trip equality and compatibility with golden payloads.
- Test version negotiation helpers with supported/unsupported combinations.
- Validate field constraints (e.g., shard overlaps, endpoint URIs) produce actionable errors.

### Integration tests
- Plug the library into registry APIs and dispatcher RPC flows to verify end-to-end compatibility.
- Run CLI commands that consume registry data to ensure parsing/output remain unchanged.
- Simulate schema upgrades by mixing old/new payloads and asserting compatibility logic.

### Feature tests
- In OmniRelay.FeatureTests, exercise registry CRUD workflows with the new library, ensuring consumer services (CLI, dashboards) display correct data.
- Validate operator workflows that export/import registry snapshots via shared codecs.

### Hyperscale Feature Tests
- Under OmniRelay.HyperscaleFeatureTests, handle large registry datasets to ensure serialization performance remains acceptable and memory usage stable.
- Stress schema evolution scenarios (rolling upgrade) to confirm compatibility helpers prevent outages.

## Implementation status
- The canonical registry records (`ShardRecord`, `ShardHistoryRecord`, `ShardMutationRequest`, `ShardRecordDiff`, etc.) plus the strategy primitives live under `src/OmniRelay/Core/Shards/**`. They are file-scoped, serialization-friendly types that the dispatcher, CLI, and tooling now consume directly, and `ShardHashStrategyRegistry` + the `JsonCodec` helpers guarantee schema parity when emitting registry snapshots or diffs.
- Configuration + code reuse is wired through `src/OmniRelay.Configuration/Sharding/ShardingConfigurationExtensions.cs`, which binds `appsettings` documents into the shared hash requests/plans. Every shard store implementation (`src/OmniRelay.ShardStore.Relational`, `src/OmniRelay.ShardStore.ObjectStorage`, `src/OmniRelay.ShardStore.Sqlite`, `src/OmniRelay.ShardStore.Postgres`) persists those records verbatim so control-plane services and governance tooling link against the same contracts.
- Coverage spans `tests/OmniRelay.Core.UnitTests/Shards/*.cs` for optimistic concurrency, diff streaming, and hashing determinism plus the hyperscale scenario in `tests/OmniRelay.HyperscaleFeatureTests/Scenarios/ShardSchemaHyperscaleFeatureTests.cs`, which drives thousands of shard assignments and rolling node updates to prove the shared schemas/codecs stay stable under load.

## References
- Existing registry models/codecs within dispatcher modules.
- `docs/architecture/service-discovery.md` - Registry schema sections.
- REFDISC-034..037 - AOT readiness baseline and CI gating.
