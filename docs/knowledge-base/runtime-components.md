# Runtime Components

## Dispatcher (`src/OmniRelay`)
- **Transports**: `Transport.Http` and `Transport.Grpc` expose unary, oneway, and streaming RPC shapes. HTTP inbound also serves `/omnirelay/introspect`, `/healthz`, `/readyz`.
- **Hugo results/concurrency**: Data plane transports and resource lease flows return `Result<T>` end-to-end (no business-logic throws). Streaming loops use Hugo `ErrGroup`, bounded channels, and `Result` streaming helpers so failures/cancellations surface as structured `Error` values that compose cleanly in Functional pipelines.
- **Codecs**: JSON (`JsonCodec`), Protobuf, and raw codecs plug into dispatcher registrations; custom codecs implement encode/decode helpers returning `Result<T>`.
- **Middleware**: Logging, tracing (`RpcTracingMiddleware`), metrics, retry/deadline enforcement, panic recovery, rate limiting, chaos toggles, and peer circuit breakers all live under `Core/Middleware` and can be applied globally or per procedure.
- **Hot-path dispatch**: Inbound pipelines are now composed and cached per procedure at registration time to keep per-request dispatch allocation-free and Native AOT friendly (aligns with `dotnet-performance-guidelines.md` R14).
- **Procedure registry**: Lookup keys are struct-based (avoids string concat) with service/kind-scoped wildcard buckets sorted by specificity, so alias resolution stays allocation-free and deterministic; keep future additions LINQ-free and span-friendly to remain AOT safe.
- **Peer & routing**: Choosers (round-robin, fewest-pending, two-random-choice), peer list watchers, and sharding helpers (resource lease components, hashing strategies) sit under `Core/Peers` and `Core/Shards`.
- **Gossip**: Membership fanout selection uses reservoir sampling with `ArrayPool<T>` so allocations scale with requested fanout instead of cluster size; peer-view sampling/shuffle targets use partial in-place swaps (no full-list shuffle) to keep per-round allocations near-zero. Keep gossip hot paths LINQ-free, clear pooled spans before returning, and prefer span/stack-friendly helpers for Native AOT builds.
- **ResourceLease mesh**: `ResourceLease*` contracts plus replicators (gRPC, SQLite, object storage) coordinate SafeTaskQueue workflows, failure drills, and deterministic recovery.

## Hashing & Shards (`src/OmniRelay/Core/Shards`)
- Hash strategies (rendezvous, ring, locality-aware) feed into `ShardControlPlaneService` for simulations and rebalancing.
- Repositories implement `IShardRepository` for storage-specific persistence (relational, object storage).

## Code generation
- `OmniRelay.Codegen.Protobuf` provides the `protoc-gen-omnirelay-csharp` plug-in.
- `OmniRelay.Codegen.Protobuf.Generator` is a Roslyn incremental generator that emits dispatcher/client glue at compile time.

## CLI helpers
- `OmniRelay.Cli` hosts config validation (`omnirelay config validate`), dispatcher introspection, benchmarking, scripting, node upgrade/drain flows, and the new `mesh shards *` commands feeding into shard diagnostics.
- `omnirelay serve` now checks `RuntimeFeature.IsDynamicCodeSupported` and emits a clear error in Native AOT builds instead of invoking reflection-heavy bootstrap paths.
