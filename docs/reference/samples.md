# OmniRelay Samples

The repository ships focused sample projects that exercise specific runtime features. Each project is self-contained under `samples/` and can be launched with `dotnet run`. The table below summarizes what each sample highlights.

| Sample | Path | Focus | Highlights |
| ------ | ---- | ----- | ---------- |
| Quickstart dispatcher | `samples/Quickstart.Server` | Manual bootstrap | Registers unary/oneway/stream handlers, custom inbound middleware, mixed HTTP + gRPC inbounds. |
| Configuration host | `samples/Configuration.Server` | `AddOmniRelayDispatcher` + DI | Uses `appsettings.json` to configure transports, diagnostics, middleware, JSON codecs, and a custom outbound spec instantiated via configuration. |
| Tee shadowing | `samples/Shadowing.Server` | `TeeUnaryOutbound` / `TeeOnewayOutbound` | Mirrors production calls to a shadow stack, shows how to compose typed clients and oneway fan-out while logging both inbound and outbound pipelines. |
| Distributed demo | `samples/DistributedDemo` | Docker Compose + multi-service topology | Gateway + downstream services communicating via gRPC (Protobuf) and HTTP (JSON), multiple peer choosers, OpenTelemetry collector, and Prometheus scraping. |

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
