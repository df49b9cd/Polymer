# OmniRelay

OmniRelay is the .NET port of Uber's YARPC runtime, layered on top of Hugo concurrency primitives. It targets transport, middleware, and tooling parity with `yarpc-go` while embracing .NET idioms (Generic Host, System.CommandLine, Roslyn generators).

> Namespaces live under `OmniRelay.*`. NuGet packages, tooling, and assemblies publish as `OmniRelay.*`.

## Current Status

- HTTP and gRPC transports covering unary, oneway, and server/client/duplex streaming behind a single dispatcher.
- Middleware set for logging, tracing, metrics, deadlines, retries, panic recovery, and rate limiting across every RPC shape.
- Codec registry with JSON, protobuf, and raw codecs, including alias metadata surfaced through introspection.
- Peer management with round-robin, fewest-pending, and two-random-choice choosers, circuit breakers, and per-peer metrics.
- Operator tooling: `/omnirelay/introspect`, `/healthz`, `/readyz`, the `omnirelay` CLI, and configuration binder for DI hosting.
- Protobuf automation via a `protoc` plugin and Roslyn incremental generator that emit dispatcher registration helpers and typed clients.
- Upcoming: richer diagnostics toggles, sample services, cross-language conformance harnesses, and CI matrix coverage (see `docs/todo.md`).

## Repository Layout

- `src/OmniRelay` - builds `OmniRelay.dll`; contains dispatcher, transports, codecs, middleware, peer subsystem, and client helpers.
- `src/OmniRelay.Configuration` - builds `OmniRelay.Configuration.dll`; contains `AddOmniRelayDispatcher`, configuration models, and spec hooks (`ICustomInboundSpec`, etc.).
- `src/OmniRelay.Cli` - builds the `OmniRelay.Cli` global tool (`omnirelay` command) for config validation, introspection, and scripted smoke tests.
- `src/OmniRelay.Codegen.Protobuf` - builds the `OmniRelay.Codegen.Protobuf` console plug-in (`protoc-gen-omnirelay-csharp`).
- `src/OmniRelay.Codegen.Protobuf.Generator` - Roslyn incremental generator package (ships OmniRelay runtime dependencies).
- `tests/OmniRelay.Tests` - xUnit coverage across transports, middleware, peer logic, codecs, configuration, and codegen.
- `tests/OmniRelay.YabInterop` - yab-driven HTTP/gRPC interop harness.
- `docs/` - architecture plan, backlog, and reference guides (streaming, middleware, diagnostics, shadowing, etc.).

## Build & Test

```bash
dotnet build OmniRelay.slnx
dotnet test tests/OmniRelay.Tests/OmniRelay.Tests.csproj
```

OmniRelay targets `.NET 10` and pulls Hugo, gRPC, and JsonSchema.Net from NuGet. Tests expect loopback HTTP/2 support for gRPC scenarios.

## Samples

The `samples/` directory contains runnable projects that focus on different runtime features (manual bootstrap, configuration-driven hosting, tee/shadow outbounds, multi-service Docker demos). See `docs/reference/samples.md` for a tour and usage guide.

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

```jsonc
// appsettings.json
{
  "polymer": {
    "service": "gateway",
    "inbounds": {
      "http": [{ "urls": [ "http://0.0.0.0:8080" ] }],
      "grpc": [{ "urls": [ "http://0.0.0.0:9090" ] }]
    },
    "outbounds": {
      "keyvalue": {
        "unary": {
          "grpc": [{
            "service": "keyvalue",
            "addresses": [ "http://keyvalue:9090" ],
            "peerChooser": "round-robin"
          }]
        }
      }
    },
    "middleware": {
      "inbound": {
        "unary": [ "tracing", "metrics" ],
        "oneway": [ "logging" ]
      }
    }
  }
}
```

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLogging();
builder.Services.AddOmniRelayDispatcher(builder.Configuration.GetSection("polymer"));

var app = builder.Build();
await app.RunAsync();
```

`OmniRelay.Configuration` exposes spec interfaces (`ICustomInboundSpec`, `ICustomOutboundSpec`, `ICustomPeerChooserSpec`) so transports and peer choosers can be registered in DI and referenced by name.

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
- `src/OmniRelay.Codegen.Protobuf.Generator` packages the same emitter as a Roslyn incremental generator. Reference it as an analyzer, generate descriptor sets via `Grpc.Tools`, and add them to `AdditionalFiles` to light up IntelliSense-friendly clients. See `tests/OmniRelay.Tests/Projects/ProtobufIncrementalSample`.

## Observability

- gRPC transports ship logging, tracing, and metrics interceptors (`GrpcTelemetryOptions`) that emit OpenTelemetry-compatible `Activity` and `Meter` data.
- Peer leasing feeds `OmniRelay.Core.Peers` counters and the dispatcher readiness evaluator (`/readyz`).
- Introspection surfaces peer health summaries (success/failure counts, latency percentiles) along with middleware, codec, and tee/shadow wiring. The CLI can print a summary or raw JSON snapshot.

## Roadmap

Active backlog lives in `docs/todo.md`. Near-term focus:

- OpenTelemetry exporter helpers and logging enrichers.
- Sample services demonstrating configuration-driven wiring and middleware composition.
- Cross-language conformance + benchmarking harnesses against `yarpc-go`.
- CI matrix builds across OS/runtime combinations with analyzer + formatting gates.

## Further Reading

- `docs/plan.md` - architecture deep dive and parity milestones.
- `docs/reference/streaming.md` - unary/server/client/duplex streaming guidance.
- `docs/reference/middleware.md` - composition rules and builder APIs.
- `docs/reference/errors.md` - status mapping, adapters, and fault helpers.
- `docs/reference/shadowing.md` - tee/shadow outbounds and sampling controls.

Contributions welcome - capture findings in `docs/todo.md` or open an issue when new gaps surface.
