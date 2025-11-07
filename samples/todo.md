# Samples Backlog

Curated backlog of sample projects that demonstrate OmniRelay end-to-end patterns. Each entry highlights the target audience, the runtime slices it exercises, and any critical teaching goals.

## Minimal API Bridge (shipped)

- Implemented at `samples/MinimalApiBridge` with docs in `docs/reference/samples.md`.
- Demonstrates ASP.NET Core Minimal APIs and OmniRelay sharing the same Generic Host, DI registrations, and handler classes for both REST and RPC traffic.

## Config-to-Prod Template

- **Audience:** Platform teams standardizing dispatcher hosting.
- **What to show:** Layer `appsettings.*` files, `AddOmniRelayDispatcher`, environment overrides, diagnostics toggles, and Docker/Kubernetes liveness probes.
- **Why it helps:** Provides a copy/paste deployment skeleton with health endpoints, readiness gates, and structured configuration so new services launch consistently.

## Streaming Analytics Lab

- **Audience:** Real-time telemetry/quant teams.
- **What to show:** Server, client, and duplex streaming handlers plus matching clients that publish/consume ticker or ESG updates using JSON + protobuf codecs.
- **Why it helps:** Demonstrates backpressure, cancellation, and codec reuse without deep-diving into the streaming guide first.

## Observability & CLI Playground

- **Audience:** SREs and operators.
- **What to show:** `/omnirelay/introspect`, `/healthz`, `/readyz`, OpenTelemetry/Prometheus wiring, and prebuilt `omnirelay` CLI scripts for config validation, smoke tests, and benchmarking.
- **Why it helps:** Lowers the barrier for operational sign-off and gives teams reproducible diagnostics flows tied to OmniRelay’s tooling.

## Codegen + Tee Rollout Harness

- **Audience:** API platform teams performing migrations.
- **What to show:** Protobuf contracts compiled via the CLI/`protoc` plugin, Roslyn incremental generator output, typed clients, and tee/shadow outbounds validating a new deployment before cutover.
- **Why it helps:** Connects code generation, typed clients, and safe rollout mechanics in one place so adopters understand the full toolchain.

## Multi-Tenant Gateway Sample

- **Audience:** SaaS or bank platforms consolidating many services.
- **What to show:** Dispatcher hosting multiple tenant-specific procedures that read routing metadata, apply per-tenant middleware (quotas, logging), and fan out to isolated peer sets.
- **Why it helps:** Highlights OmniRelay’s ability to enforce tenant isolation, rate limits, and custom peer choosers without duplicating hosts.

## Hybrid Batch + Realtime Runner

- **Audience:** Teams blending scheduled jobs with live RPCs.
- **What to show:** Background workers enqueue batch jobs through OmniRelay oneway procedures, while a live dashboard consumes server streams to follow progress; include retry budgets and deadlines.
- **Why it helps:** Demonstrates that the runtime can coordinate asynchronous workloads and live traffic inside the same host with consistent middleware.

## Chaos & Failover Lab

- **Audience:** Reliability engineers.
- **What to show:** Scripts that deliberately fail peers, trip circuit breakers, and observe peer chooser behavior plus automatic recovery via `/omnirelay/introspect` and metrics.
- **Why it helps:** Gives teams a safe sandbox to learn how OmniRelay reacts to partial outages, ensuring confidence before production rollouts.
