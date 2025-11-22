# OmniRelay

![OmniRelay hero](branding/omnirelay-hero.png)

[![Build](https://github.com/df49b9cd/Polymer/actions/workflows/publish-packages.yml/badge.svg?branch=main)](https://github.com/df49b9cd/Polymer/actions/workflows/publish-packages.yml)
[![codecov](https://codecov.io/gh/df49b9cd/Polymer/branch/main/graph/badge.svg)](https://codecov.io/gh/df49b9cd/OmniRelay)
[![NuGet OmniRelay](https://img.shields.io/nuget/v/OmniRelay.svg)](https://www.nuget.org/packages/OmniRelay)
[![NuGet OmniRelay.Codegen.Generator](https://img.shields.io/nuget/v/OmniRelay.Codegen.Generator.svg)](https://www.nuget.org/packages/OmniRelay.Codegen.Protobuf.Generator)
[![License](https://img.shields.io/github/license/df49b9cd/Polymer.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-blueviolet.svg)](https://dotnet.microsoft.com/)

OmniRelay is the .NET port of Uber's YARPC runtime, layered on top of Hugo concurrency primitives. It targets transport, middleware, and tooling parity with `yarpc-go` while embracing .NET idioms (Generic Host, System.CommandLine, Roslyn generators).

> Namespaces live under `OmniRelay.*`. NuGet packages, tooling, and assemblies publish as `OmniRelay.*`.

## Current Feature Set

- **Transports + codecs**: HTTP and gRPC transports covering unary, oneway, and server/client/duplex streaming with JSON, Protobuf, and raw codecs registered through the dispatcher.
- **Middleware**: Logging, tracing, metrics, deadlines, retries, panic recovery, rate limiting, and peer-circuit breakers applied uniformly across every RPC shape.
- **Peer + routing layer**: Round-robin, fewest-pending, and two-random-choice choosers; chaos labs; sharding helpers (`ShardedResourceLeaseReplicator`, `BackpressureAwareRateLimiter`) to segment workloads per tenant/resource.
- **ResourceLease mesh**: Resource-neutral `ResourceLease*` contracts, SafeTaskQueue dispatcher component, durable replicators (SQLite, gRPC, object storage), deterministic state stores (SQLite/FileSystem with Cosmos/Redis guidance), control-plane endpoints, and failure drills. See `docs/architecture/omnirelay-rpc-mesh.md`.
- **Diagnostics + observability**: `/omnirelay/introspect`, `/healthz`, `/readyz`, runtime tracing/logging toggles, Prometheus + OTLP exporters, mesh health dashboard guidance, and governance-ready replication sinks.
- **Tooling**: `omnirelay` CLI, source-generated configuration loading, and automation scripts for drain/restore/upgrade flows.
- **Code generation**: Protobuf `protoc` plug-in + Roslyn incremental generator emitting typed OmniRelay clients and dispatcher registration helpers.
- **CI status**: Active backlog lives in `todo.md`; new work focuses on conformance harnesses and expanded sample coverage.

## Repository Layout

- `src/OmniRelay` - builds `OmniRelay.dll`; contains dispatcher, transports, codecs, middleware, peer subsystem, and client helpers.
- `src/OmniRelay.Cli` - builds the `OmniRelay.Cli` global tool (`omnirelay` command) for config validation, introspection, and scripted smoke tests.
- `src/OmniRelay.Codegen.Protobuf` - builds the `OmniRelay.Codegen.Protobuf` console plug-in (`protoc-gen-omnirelay-csharp`).
- `src/OmniRelay.Codegen.Protobuf.Generator` - Roslyn incremental generator package (ships OmniRelay runtime dependencies).
- `tests/OmniRelay.Core.UnitTests` - xUnit coverage across transports, middleware, peer logic, codecs, configuration, and codegen.
- `tests/OmniRelay.YabInterop` - yab-driven HTTP/gRPC interop harness.
- `docs/` - architecture plan, backlog, and reference guides (streaming, middleware, diagnostics, shadowing, etc.).

## Build & Test

```bash
dotnet build OmniRelay.slnx
dotnet test tests/OmniRelay.Core.UnitTests/OmniRelay.Core.UnitTests.csproj
```

OmniRelay targets `.NET 10` and pulls Hugo, gRPC, and JsonSchema.Net from NuGet. Tests expect loopback HTTP/2 support for gRPC scenarios.

### Reproduce CI Locally

Use the Docker CI recipe to match the pipeline’s restore/build/test steps:

```bash
./eng/run-ci.sh --build-arg DOTNET_VERSION=10.0.100
```

Pass any additional `docker build` flags after the script (for example, `--progress=plain`).
Add `--arch x64` or `--arch arm64` to force the platform (defaults to your host).
Docker logging levels map to `docker build --progress=<level>`:

- `auto` (default) – Docker chooses between TTY/plain depending on the terminal.
- `plain` – line-oriented logs, useful for CI and truncation-free output.
- `tty` – interactive TTY view with live-updating build steps.

### Native AOT Builds

OmniRelay is AOT-first for cloud-native deployments. Run the shared helper to produce native binaries (defaults to `linux-x64`):

```bash
./eng/run-aot-publish.sh
./eng/run-aot-publish.sh linux-arm64 Release
```

See `docs/architecture/aot-guidelines.md` for trimming guidance, contributor expectations, and troubleshooting tips.

### Hyperscale Smoke Testing

Use the hyperscale CI Dockerfile to run feature/integration/hyperscale suites in a controlled container:

```bash
docker build -f docker/Dockerfile.hyperscale.ci .
```

Internally it calls `eng/run-hyperscale-smoke.sh`, which executes the representative chaos/diagnostics suites to validate control-plane readiness.

## Samples

The legacy `samples/` tree has been removed while we refresh the runtime for Native AOT and source-generated configuration. New sample hosts will return once the refreshed setup is stable; refer to the hosting examples below in the interim.

## Hosting A Dispatcher

### Manual wiring

```csharp
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Http;
using OmniRelay.Transport.Grpc;
using static Hugo.Go;

public record PostLedgerEntryRequest(string AccountId, decimal Amount);
public record AckResponse(string Status);

var options = new DispatcherOptions("ledger");

// Inbounds expose HTTP and gRPC entry points plus health endpoints.
options.AddLifecycle("http-inbound", new HttpInbound(new[] { "http://0.0.0.0:8080" }));
options.AddLifecycle("grpc-inbound", new GrpcInbound(new[] { "http://0.0.0.0:9090" }));

// Outbounds can mix transports; gRPC automatically load-balances peers.
options.AddUnaryOutbound(
    "payments",
    null,
    new GrpcOutbound(new[] { new Uri("http://payments-grpc:9090") }, "payments"));

options.AddOnewayOutbound(
    "audit",
    null,
    new HttpOutbound(
        new HttpClient { BaseAddress = new Uri("http://audit:8080") },
        requestUri: new Uri("http://audit:8080/yarpc/v1/billing::record")));

// Global middleware applies to every procedure; builders layer per-procedure middleware.
options.UnaryInboundMiddleware.Add(new RpcTracingMiddleware());
options.UnaryInboundMiddleware.Add(new RpcMetricsMiddleware());

var dispatcher = new Dispatcher(options);
var codec = new JsonCodec<PostLedgerEntryRequest, AckResponse>();

dispatcher.RegisterUnary(
    "ledger::post-entry",
    builder => builder
        .WithEncoding(codec.Encoding)
        .Handle(async (request, ct) =>
        {
            var decode = codec.DecodeRequest(request.Body, request.Meta);
            if (decode.IsFailure)
            {
                return Err<Response<ReadOnlyMemory<byte>>>(decode.Error!);
            }

            // Apply domain logic (omitted)
            await Task.CompletedTask.ConfigureAwait(false);

            var responseMeta = new ResponseMeta(encoding: codec.Encoding);
            var encode = codec.EncodeResponse(new AckResponse("ok"), responseMeta);
            if (encode.IsFailure)
            {
                return Err<Response<ReadOnlyMemory<byte>>>(encode.Error!);
            }

            return Ok(Response<ReadOnlyMemory<byte>>.Create(new ReadOnlyMemory<byte>(encode.Value), responseMeta));
        }));

await dispatcher.StartAsync();
// ...
await dispatcher.StopAsync();
```

The HTTP inbound exposes `/omnirelay/introspect`, `/healthz`, and `/readyz`. Introspection snapshots list procedures, middleware stacks, codec aliases, transport diagnostics (including tee/shadow metadata), and per-peer metrics.

### Hosting via configuration

Configuration-driven hosting now uses trimming-safe, source-generated binding. Point `AddOmniRelayDispatcherFromConfig("appsettings.dispatcher.json")` at a dispatcher JSON payload and register any custom middleware/interceptors in DI. Updated samples will land after the ongoing AOT refactor.

## Tooling

- `omnirelay config validate --config appsettings.json --config appsettings.Development.json`
- `omnirelay introspect --url http://127.0.0.1:8080/omnirelay/introspect --format text`
- `omnirelay request --transport grpc --address http://127.0.0.1:9090 --service echo --procedure Ping --profile protobuf:echo.EchoRequest --proto-file descriptors/echo.protoset --body '{"message":"cli"}'`
- `omnirelay benchmark --transport http --url http://127.0.0.1:8080/yarpc/v1 --service echo --procedure echo::ping --profile json:pretty --body '{"message":"load"}' --concurrency 20 --requests 500`
- `omnirelay script run --file docs/reference/cli-scripts/echo-harness.json --dry-run`

Install locally with:

```bash
dotnet pack src/OmniRelay.Cli/OmniRelay.Cli.csproj -c Release -o artifacts/cli
dotnet tool install --global OmniRelay.Cli --add-source artifacts/cli
```

See `docs/reference/cli.md` for profiles, protobuf automation, and CI recipes.

## Protobuf Automation

- `src/OmniRelay.Codegen.Protobuf` provides `protoc-gen-omnirelay-csharp`, which emits dispatcher registration helpers (`Register<Service>`), service interfaces, and typed OmniRelay clients (unary + streaming) with codecs pre-wired.
- `src/OmniRelay.Codegen.Protobuf.Generator` packages the same emitter as a Roslyn incremental generator. Reference it as an analyzer, generate descriptor sets via `Grpc.Tools`, and add them to `AdditionalFiles` to light up IntelliSense-friendly clients. The `tests/OmniRelay.Codegen.Tests` project shows the descriptor-set pattern used in CI.

## Observability

- gRPC transports ship logging, tracing, and metrics interceptors (`GrpcTelemetryOptions`) that emit OpenTelemetry-compatible `Activity` and `Meter` data.
- Peer leasing feeds `OmniRelay.Core.Peers` counters and the dispatcher readiness evaluator (`/readyz`).
- Introspection surfaces peer health summaries (success/failure counts, latency percentiles) along with middleware, codec, and tee/shadow wiring. The CLI can print a summary or raw JSON snapshot.

## Roadmap

Active backlog lives in `todo.md`. Near-term focus:

- OpenTelemetry exporter helpers and logging enrichers.
- Sample services demonstrating configuration-driven wiring and middleware composition.
- Cross-language conformance + benchmarking harnesses against `yarpc-go`.
- CI matrix builds across OS/runtime combinations with analyzer + formatting gates.

## Further Reading

- `docs/reference/index.md` - docs index for key topics.
- `docs/architecture/omnirelay-rpc-mesh.md` - end-to-end ResourceLease mesh architecture (replication, diagnostics, failure drills).
- `docs/reference/http-transport.md` - TLS, proxy placement, SSE behaviour, and tracing guidance for the HTTP transport.
- `docs/reference/http3-developer-guide.md` - enabling HTTP/3 locally and in staging/production with prerequisites and troubleshooting.
- `docs/reference/http3-faq.md` - HTTP/3/QUIC troubleshooting FAQ (ALPN, UDP, macOS, curl, etc.).
- `docs/reference/streaming.md` - unary/server/client/duplex streaming guidance.
- `docs/reference/middleware.md` - composition rules and builder APIs.
- `docs/reference/errors.md` - status mapping, adapters, and fault helpers.
- `docs/reference/shadowing.md` - tee/shadow outbounds and sampling controls.
- `docs/reference/grpc-compatibility.md` - current status of gRPC client HTTP/3 support across languages.

Contributions welcome - capture findings in `todo.md` or open an issue when new gaps surface.
