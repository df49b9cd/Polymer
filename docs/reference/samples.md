# OmniRelay Samples

The repository ships focused sample projects that exercise specific runtime features. Each project is self-contained under `samples/` and can be launched with `dotnet run`. The table below summarizes what each sample highlights.

| Sample | Path | Focus | Highlights |
| ------ | ---- | ----- | ---------- |
| Quickstart dispatcher | `samples/Quickstart.Server` | Manual bootstrap | Registers unary/oneway/stream handlers, custom inbound middleware, mixed HTTP + gRPC inbounds. |
| Minimal API bridge | `samples/MinimalApiBridge` | ASP.NET Core + OmniRelay | Shared DI container hosts Minimal APIs next to an OmniRelay dispatcher so HTTP controllers and RPC procedures reuse the same handlers, codecs, and middleware. |
| Streaming analytics lab | `samples/StreamingAnalytics.Lab` | Server/client/duplex streaming | Demonstrates JSON + Protobuf codecs, server/client/duplex handlers, and matching OmniRelay streaming clients that feed ESG/ticker data. |
| Config-to-prod template | `samples/ConfigToProd.Template` | Layered config + probes | Shows `AddOmniRelayDispatcher` with `appsettings.*`, env overrides, diagnostics toggles, and liveness/readiness endpoints ready for Docker/Kubernetes. |
| Multi-tenant gateway | `samples/MultiTenant.Gateway` | Tenant-aware routing | Demonstrates per-tenant middleware, quotas, and routing headers that fan out to isolated peer sets without duplicating hosts. |
| Hybrid batch runner | `samples/HybridRunner` | Batch + realtime | Background jobs push progress via oneway RPCs while dashboards stream updates via server streams. |
| Chaos & failover lab | `samples/ChaosFailover.Lab` | Reliability sandbox | Spins up flaky backends so retries, deadlines, and peer diagnostics can be observed safely. |
| Configuration host | `samples/Configuration.Server` | `AddOmniRelayDispatcher` + DI | Uses `appsettings.json` to configure transports, diagnostics, middleware, JSON codecs, and a custom outbound spec instantiated via configuration. |
| Codegen + tee rollout | `samples/CodegenTee.Rollout` | Protobuf generator + shadowing | Builds Protobuf contracts via OmniRelay’s generator and mirrors typed client calls to primary + shadow deployments using tee outbounds. |
| Tee shadowing | `samples/Shadowing.Server` | `TeeUnaryOutbound` / `TeeOnewayOutbound` | Mirrors production calls to a shadow stack, shows how to compose typed clients and oneway fan-out while logging both inbound and outbound pipelines. |
| Distributed demo | `samples/DistributedDemo` | Docker Compose + multi-service topology | Gateway + downstream services communicating via gRPC (Protobuf) and HTTP (JSON), multiple peer choosers, OpenTelemetry collector, and Prometheus scraping. |
| Observability & CLI playground | `samples/Observability.CliPlayground` | Diagnostics + scripts | Exposes `/omnirelay/introspect`, `/healthz`, `/readyz`, Prometheus metrics, OpenTelemetry traces, and ships ready-made `omnirelay` CLI scripts. |
| ResourceLease mesh demo | `samples/ResourceLease.MeshDemo` | ResourceLease RPC mesh | Shows resource-neutral lease contracts, durable SQLite replication + deterministic store, role-based dispatcher/diagnostics/seeder/worker processes, Docker Compose lab with Prometheus/Grafana dashboards, Native AOT container images, backpressure hooks, and background workers exercising `resourcelease::*` procedures. |

## Quickstart Dispatcher

- Path: `samples/Quickstart.Server`
- Run: `dotnet run --project samples/Quickstart.Server`
- What it shows:
  - Manual `DispatcherOptions` bootstrap with HTTP + gRPC inbounds.
  - Direct registration of unary, oneway, and server-streaming procedures via `RegisterJsonUnary`, `RegisterOneway`, and `RegisterStream`.
  - Console middleware implementing `IUnaryInboundMiddleware`, `IOnewayInboundMiddleware`, and `IStreamInboundMiddleware`.
  - Custom peer chooser (`FewestPendingPeerChooser`) wired into a gRPC outbound.

## Configuration-Driven Host

- Path: `samples/Configuration.Server`
- Run: `dotnet run --project samples/Configuration.Server`
- Features covered:
  - Generic Host + dependency injection via `Host.CreateDefaultBuilder`.
  - `AddOmniRelayDispatcher(builder.Configuration.GetSection("omnirelay"))` binding the dispatcher, transports, middleware, and codecs from `appsettings.json`.
  - JSON codec registrations configured under `encodings:json` and resolved at runtime via `dispatcher.Codecs.TryResolve(...)`.
  - Diagnostics and control plane enabled declaratively (`diagnostics.runtime`, OTLP/Prometheus exporters).
  - `ICustomOutboundSpec` implementation (`audit-fanout`) that materializes an HTTP oneway outbound with configured headers.
  - Shared middleware (`RequestLoggingMiddleware`) resolved from DI because the configuration lists the concrete type.
  - Hosted services that register procedures on start-up and emit a startup banner describing inbound bindings.
- Notes:
  - Replace the placeholder downstream addresses in `appsettings.json` (`inventory`, `audit`) before running against real services.
  - The sample demonstrates best-effort fire-and-forget telemetry fan-out; production code should apply retry/metrics middleware as appropriate.

## Minimal API Bridge

- Path: `samples/MinimalApiBridge`
- Run: `dotnet run --project samples/MinimalApiBridge`
- What it shows:
  - ASP.NET Core Minimal APIs running side-by-side with an OmniRelay dispatcher inside the same Generic Host.
  - HTTP controllers (`/api/greetings`, `/api/portfolios`) and OmniRelay procedures (`greetings::say`, `portfolio::rebalance`, `alerts::emit`) reuse the same handler classes and JSON codecs from DI.
  - A hosted service that starts/stops the dispatcher plus REST endpoints (`/api/dispatcher`) that report transport bindings for quick smoke tests.
  - Example of bridging existing REST traffic into OmniRelay without rewriting every caller on day one.
- Notes:
  - The Minimal API host listens on `http://127.0.0.1:5058` by default; override via `ASPNETCORE_URLS`.
  - OmniRelay HTTP/gRPC inbounds default to `http://127.0.0.1:7080` and `http://127.0.0.1:7090`; adjust the URIs in `Program.cs` to match your environment.

## Streaming Analytics Lab

- Path: `samples/StreamingAnalytics.Lab`
- Run: `dotnet run --project samples/StreamingAnalytics.Lab`
- What it shows:
  - Server-, client-, and duplex-streaming handlers living in the same dispatcher, mixing JSON codecs (ticker subscriptions) with Protobuf codecs generated from `Protos/analytics.proto`.
  - Use of `StreamClient`, `ClientStreamClient`, and `DuplexStreamClient` to call the dispatcher through loopback gRPC outbounds, illustrating how to reuse codecs/middleware for inbound and outbound streaming.
  - Backpressure-aware server streaming that throttles ticker updates, client-stream aggregation that reads `MetricSample` batches, and duplex collaboration that broadcasts `InsightSignal` replies as requests arrive.
- Notes:
  - gRPC inbound/outbound listen on `http://127.0.0.1:7190`. Update the URIs inside `StreamingLabBootstrap` if you need to expose different ports.
  - The bundled demo clients run automatically, but you can connect your own OmniRelay/gRPC clients using the same JSON contract (`TickerSubscription`/`TickerUpdate`) and the generated Protobuf messages.

## Config-to-Prod Template

- Path: `samples/ConfigToProd.Template`
- Run: `dotnet run --project samples/ConfigToProd.Template --environment Development`
- What it shows:
  - Explicit configuration layering (`appsettings.json`, `appsettings.{Environment}.json`, environment variables, command-line) prior to calling `AddOmniRelayDispatcher`.
  - OmniRelay transports, middleware, diagnostics, and codecs expressed entirely in configuration, keeping dev/staging/prod consistent apart from overrides.
  - ASP.NET Core host that exposes `/healthz` (liveness) and `/readyz` (readiness) endpoints plus a dispatcher health check suitable for Docker/Kubernetes probes.
  - Runtime diagnostics toggles managed via `IOptionsMonitor`, feeding readiness policy so `/readyz` can fail when metrics must be enabled.
- Notes:
  - Prefix any environment variable with `CONFIG2PROD_` (double underscores become `:`) to override configuration without touching files.
  - Adjust warm-up and diagnostics requirements via the `probes` section to mirror your deployment policy.

## Observability & CLI Playground

- Path: `samples/Observability.CliPlayground`
- Run: `dotnet run --project samples/Observability.CliPlayground --environment Development`
- What it shows:
  - OmniRelay hosted via configuration with HTTP `/omnirelay/introspect`, `/healthz`, `/readyz`, gRPC inbound, and Prometheus metrics (`/metrics`).
  - OpenTelemetry configuration (console trace exporter + Prometheus metrics exporter) so telemetry lights up by default.
  - Background service that simulates `omnirelay` CLI flows (introspection + ops ping) to generate metrics and ensure endpoints can be scripted.
  - Companion CLI script (`docs/reference/cli-scripts/observability-playground.json`) for reproducible health/introspection/request steps.
- Notes:
  - Override ports or telemetry settings via `OBS_CLI__` environment variables.
  - Pair the sample with `omnirelay benchmark` to record traces/metrics during load tests.

## Multi-Tenant Gateway

- Path: `samples/MultiTenant.Gateway`
- Run: `dotnet run --project samples/MultiTenant.Gateway`
- What it shows:
  - Dispatcher hosting multiple tenants identified via `x-tenant-id` headers.
  - Per-tenant middleware enforcing quotas/logging without duplicating hosts.
  - Tenant-specific HTTP outbounds so each tenant fans out to its own peer set.
- Notes:
  - Update the placeholder HTTP endpoints (`http://localhost:7201` / `7202`) to actual services.
  - Bind tenant settings from configuration to mirror production environments.

## Hybrid Batch + Realtime Runner

- Path: `samples/HybridRunner`
- Run: `dotnet run --project samples/HybridRunner`
- What it shows:
  - Oneway `batch::enqueue` procedure queuing batch jobs with deadlines and retry budgets enforced via middleware.
  - Background worker processing queued jobs and emitting progress messages.
  - Server-streaming `dashboard::stream` procedure that dashboards can subscribe to in order to follow job progress live.
- Notes:
  - Adjust the `BatchWorker` delay/logic to reflect real work or wire the queues to external systems (Redis, SQS, etc.).
  - Combine with the Observability sample to expose metrics/traces for the worker pipeline.

## Chaos & Failover Lab

- Path: `samples/ChaosFailover.Lab`
- Run: `dotnet run --project samples/ChaosFailover.Lab`
- What it shows:
  - Two unstable backends (different success rates) behind a dispatcher that applies retries and deadlines.
  - Traffic generator that continuously calls `chaos::ping`, sometimes targeting the secondary outbound to trigger failover.
  - Easy hooks for `omnirelay` CLI/`/omnirelay/introspect` to observe peer diagnostics while failures occur.
- Notes:
  - Tweak backend success rates or retry/deadline values to explore different failure scenarios.
  - Pair with Observability sample to stream metrics/traces while chaos testing.

## Codegen + Tee Rollout Harness

- Path: `samples/CodegenTee.Rollout`
- Run: `dotnet run --project samples/CodegenTee.Rollout`
- What it shows:
  - `risk.proto` compiled into a descriptor set and consumed by `OmniRelay.Codegen.Protobuf.Generator`, emitting the `RiskServiceOmniRelay` interface plus a typed client.
  - Two in-process risk deployments implementing the generated interface; the harness uses `AddTeeUnaryOutbound` to fan out calls to a shadow deployment while returning the primary response.
  - Demonstration of reading both primary and shadow responses so platform teams can compare scoring logic before cutover.
- Notes:
  - Edit the proto and rebuild to regenerate the OmniRelay bindings automatically.
  - Replace the in-process outbound implementation with real gRPC outbounds when shadowing actual services.

## Tee Shadowing Sample

- Path: `samples/Shadowing.Server`
- Run: `dotnet run --project samples/Shadowing.Server`
- Highlights:
  - Declares `TeeUnaryOutbound` to mirror payment RPCs from a primary gRPC stack to a shadow deployment while preserving caller responses.
  - Declares `TeeOnewayOutbound` to broadcast audit events to a primary and beta stack with optional sampling and header injection.
  - Shows how to compose typed clients (`CreateUnaryClient`, `CreateOnewayClient`) alongside JSON codecs in order to call configured outbounds.
  - Demonstrates translating upstream failures into `OmniRelayException` and wiring outbound metadata (session IDs, shadow headers).
  - Includes console middleware that attaches to both inbound and outbound pipelines to visualize tee activity.
- Notes:
  - Update the hard-coded endpoints (`http://127.0.0.1:20000`, etc.) to point at real primary/shadow services before exercising the sample.
  - The tee mirror runs asynchronously; failing shadow calls are absorbed while primary responses continue flowing to callers.

## Distributed Demo

- Path: `samples/DistributedDemo`
- Run: `docker compose up` (from the sample directory)
- Highlights:
  - Gateway service uses configuration-driven dispatcher hosting with JSON inbound codecs and Protobuf outbound clients.
  - Inventory replicas demonstrate gRPC peer choosers (`fewest-pending`) across multiple containers.
  - Audit service consumes HTTP oneway calls with JSON codecs.
  - Docker Compose stack provisions an OpenTelemetry collector, Prometheus, and Grafana (preloaded dashboard) for observability; each service exposes `/omnirelay/metrics` for scraping.
- Notes:
  - Update the compose file or appsettings if you need to change exposed ports.
  - Traces are logged by the collector, while Prometheus provides dashboard-ready metrics on `http://localhost:9090`.
