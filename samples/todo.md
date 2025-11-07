# Samples Backlog

Curated backlog of sample projects that demonstrate OmniRelay end-to-end patterns. Each entry highlights the target audience, the runtime slices it exercises, and any critical teaching goals.

## Minimal API Bridge (shipped)

- Implemented at `samples/MinimalApiBridge` with docs in `docs/reference/samples.md`.
- Demonstrates ASP.NET Core Minimal APIs and OmniRelay sharing the same Generic Host, DI registrations, and handler classes for both REST and RPC traffic.

## Config-to-Prod Template (shipped)

- Implemented at `samples/ConfigToProd.Template`, covering layered configuration, diagnostics toggles, `/healthz` + `/readyz`, and OmniRelay hosting via `AddOmniRelayDispatcher`.

## Streaming Analytics Lab

- **Status:** Shipped at `samples/StreamingAnalytics.Lab`.
- **Highlights:** JSON ticker streams, Protobuf client/duplex handlers, and loopback streaming clients that exercise OmniRelay codecs and backpressure end-to-end.

## Observability & CLI Playground (shipped)

- Implemented at `samples/Observability.CliPlayground` with HTTP/gRPC endpoints, Prometheus/OpenTelemetry wiring, and CLI scripts under `docs/reference/cli-scripts/observability-playground.json`.

## Codegen + Tee Rollout Harness

- **Status:** Shipped at `samples/CodegenTee.Rollout`.
- **Highlights:** Builds `risk.proto` via the OmniRelay generator, registers the generated service, and tees typed client calls to primary + shadow deployments for safe rollout rehearsals.

## Multi-Tenant Gateway Sample

- **Status:** Shipped at `samples/MultiTenant.Gateway`.
- **Highlights:** Demonstrates tenant routing via headers, per-tenant quota/logging middleware, and tenant-specific HTTP outbounds.

## Hybrid Batch + Realtime Runner

- **Audience:** Teams blending scheduled jobs with live RPCs.
- **What to show:** Background workers enqueue batch jobs through OmniRelay oneway procedures, while a live dashboard consumes server streams to follow progress; include retry budgets and deadlines.
- **Why it helps:** Demonstrates that the runtime can coordinate asynchronous workloads and live traffic inside the same host with consistent middleware.

## Chaos & Failover Lab

- **Audience:** Reliability engineers.
- **What to show:** Scripts that deliberately fail peers, trip circuit breakers, and observe peer chooser behavior plus automatic recovery via `/omnirelay/introspect` and metrics.
- **Why it helps:** Gives teams a safe sandbox to learn how OmniRelay reacts to partial outages, ensuring confidence before production rollouts.
