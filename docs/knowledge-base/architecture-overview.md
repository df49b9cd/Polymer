# Architecture Overview

OmniRelay is the .NET 10 port of Uber's YARPC runtime layered on Hugo primitives. Every assembly follows the `OmniRelay.*` prefix and targets transport/middleware/tooling parity with yarpc-go while integrating tightly with .NET Generic Host and System.CommandLine. The modern stack now splits responsibilities four ways:

1. **Hugo** supplies SafeTaskQueue and concurrency primitives.
2. **OmniRelay** is the transport/data plane delivering the stateless transport appliance (HTTP/3-first listeners, transports, middleware, CLI).
3. **Control Plane** (new) holds desired dispatcher/routing/shadowing config, validates it, manages rollout and audit, and publishes signed snapshots to OmniRelay nodes.
4. **MeshKit** provides discovery/coordination (gossip, shards, rebalancer, replication) built on OmniRelay transports; it emits membership/health signals that the control plane and data plane consume.

## Core pillars
- **Dispatcher (src/OmniRelay)** bundles transports (HTTP, gRPC), codecs (JSON, Protobuf, raw), middleware (logging, tracing, metrics, deadlines, retries, circuit breakers, panic recovery, rate limiting), and peer choosers (round-robin, fewest-pending, two-random-choice). Resource-lease subsystems, chaos labs, and sharding helpers extend the peer/routing layer.
- **Dispatcher config (source-generated)** uses `DispatcherConfig` + `DispatcherConfigMapper` to bind JSON with the built-in configuration generator and produce trimming-safe `DispatcherOptions` instances. Custom middleware/peer choosers are registered in code and referenced by key in config.
- **Diagnostics stack** spans HTTP/gRPC control planes (`/omnirelay/introspect`, `/control/*`), Prometheus/OTLP exporters, runtime toggles for logging/tracing, lease health snapshots, and mesh dashboards.
- **Tooling (src/OmniRelay.Cli)** supplies the `omnirelay` CLI for config validation, dispatcher introspection, node drain/upgrade orchestration, shard control, and scripted smoke tests.
- **Code generation** includes a Protobuf `protoc` plug-in (`OmniRelay.Codegen.Protobuf`) and a Roslyn incremental generator (`OmniRelay.Codegen.Protobuf.Generator`) that emit dispatcher registrations and strongly-typed RPC clients.

## Supporting areas
- **MeshKit modules** (shards, rebalancer, registry, failover) live in the WORK-010..WORK-016 stories and consume OmniRelay transports; operator surfaces (dashboards/CLI/probes/chaos) are tracked under WORK-017..WORK-022.
- **ResourceLease mesh** delivers SafeTaskQueue primitives, deterministic replicators (SQLite, gRPC, object storage), and failover/runbook guidance under `docs/`.
- **Samples/** showcase configuration-first hosting, bootstrap flows, tee/shadow outbounds, and multi-service demos.
- **docs/** contains architecture plans (AOT, diagnostics, service discovery), backlog references (`docs/project-board/WORK-*.md`), and runbooks for operators.

The knowledge base sections below drill into each area with component dependencies, commands, and test coverage.
