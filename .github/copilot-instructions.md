# Copilot instructions for the OmniRelay codebase

This repo is a .NET runtime modeled after Uber’s YARPC. It exposes a single Dispatcher over multiple transports (HTTP and gRPC), with unified middleware, codecs, and peer management. The important bits you need to be productive:

## Big picture architecture
- Centerpiece: the Dispatcher (`src/OmniRelay/Dispatcher/Dispatcher.cs`) routes RPCs by procedure name and applies middleware stacks per RPC shape (unary, oneway, server/client/duplex streaming).
- Inbounds (servers):
  - HTTP inbound (`src/OmniRelay/Transport/Http/HttpInbound.cs`) hosts Minimal APIs for RPC plus `/omnirelay/introspect`, `/healthz`, `/readyz`. Supports HTTP/1.1, HTTP/2, and HTTP/3 (QUIC) with MsQuic tuning via `HttpServerRuntimeOptions`/`Http3RuntimeOptions`.
  - gRPC inbound (`src/OmniRelay/Transport/Grpc/GrpcInbound.cs`) hosts a single gRPC service that dispatches arbitrary procedures. Supports HTTP/2 and HTTP/3 (QUIC). Draining rejects new calls with `StatusCode.Unavailable` + `retry-after`.
- Outbounds (clients):
  - HTTP outbound (`src/OmniRelay/Transport/Http/HttpOutbound.cs`) issues unary/oneway POSTs to a configured endpoint. It can request HTTP/3 via `HttpClientRuntimeOptions` (Version/VersionPolicy) and adds `Rpc-*` headers.
  - gRPC outbound (`src/OmniRelay/Transport/Grpc/GrpcOutbound.cs`) pools channels to multiple peers, supports peer choosers and circuit breakers, and can enable HTTP/3 with ALPN + per-request Version/VersionPolicy.
- Codecs & metadata: RPC calls pass `RequestMeta`/`ResponseMeta` (encoding, headers, deadlines, etc.). Common codecs include JSON, protobuf, and raw (`src/OmniRelay/Core`).
- Configuration binding: `src/OmniRelay.Configuration` binds appsettings into inbounds/outbounds/middleware and builds a `Dispatcher` via `AddOmniRelayDispatcher`.
- Code generation: Protobuf generator (`src/OmniRelay.Codegen.Protobuf*`) emits Register extension methods, service interfaces, and typed clients.

## Project conventions & patterns
- Procedures are strings (e.g., `service::method`) and are the primary routing key across transports.
- HTTP transport headers are standardized (`HttpTransportHeaders`, e.g., `Rpc-Procedure`, `Rpc-Encoding`, `Rpc-Protocol`, `Rpc-Transport`). Server sets `Rpc-Protocol` to the actual HTTP protocol (e.g., `HTTP/3`).
- Protocol metadata:
  - HTTP inserts `Rpc-Protocol`; gRPC inserts `rpc.protocol` into request metadata. Telemetry records `rpc.protocol`, `network.protocol.name/version`, and `network.transport` (QUIC for HTTP/3).
- Middleware composition differs by RPC shape. Use the specific builder lists on `DispatcherOptions` (Inbound/Outbound per shape).
- Peer selection and resilience are transport-agnostic: round-robin, fewest-pending, two-random-choice, and circuit breaker are in `OmniRelay.Core.Peers`.
- Draining semantics: inbounds reject new work (HTTP 503 with `Retry-After: 1`, or gRPC `Unavailable` with `retry-after=1`) while completing in-flight work.

## Code style & standards
- Follow `.editorconfig` settings (4-space indents, `var` keyword preferred, braces on new lines)
- Project targets .NET 10 with nullable reference types enabled
- Analysis mode is set to "Recommended" with code style enforcement in build (`Directory.Build.props`)
- Use implicit usings where applicable
- Public APIs should have XML documentation comments (enforced for library projects)

## HTTP/3 specifics (QUIC)
- HTTP/3 requires HTTPS and a TLS 1.3-capable certificate. Enforced both for inbounds and outbounds.
- gRPC client HTTP/3:
  - Enable via `GrpcClientRuntimeOptions { EnableHttp3 = true, RequestVersion/VersionPolicy optional }`.
  - ALPN setup and `SocketsHttpHandler.EnableMultipleHttp3Connections` are handled in `GrpcOutbound`.
- HTTP client HTTP/3:
  - Enable per-request via `HttpClientRuntimeOptions` (Version and VersionPolicy). Defaults to `RequestVersionOrHigher` when `EnableHttp3` is true.

## Critical workflows
- Build & test:
  - `dotnet build OmniRelay.slnx`
  - `dotnet test tests/OmniRelay.Tests/OmniRelay.Tests.csproj`
- CLI tooling (`src/OmniRelay.Cli`):
  - Validate config: `omnirelay config validate --config appsettings.json --section polymer`
  - Introspect a running HTTP inbound: `omnirelay introspect --url http://127.0.0.1:8080/omnirelay/introspect --format text`
  - Ad-hoc requests/benchmarks: see `docs/reference/cli.md` and `src/OmniRelay.Cli/Program.cs` for supported flags (profiles, protobuf, --http3/--grpc-http3, etc.).
- Running servers in tests generally use self-signed certs and random loopback ports; QUIC/HTTP/3 tests require an OS/runtime with MsQuic support.
- CI/CD pipelines:
  - `.github/workflows/ci.yml` - runs build and tests on PRs
  - `.github/workflows/publish-packages.yml` - publishes NuGet packages on releases

## Where to look when adding features
- Add/modify transports: `src/OmniRelay/Transport/Http/*` and `src/OmniRelay/Transport/Grpc/*`; runtime options are surfaced via `*RuntimeOptions` and bound in `Configuration/Internal/DispatcherBuilder.cs`.
- Add middleware: implement the appropriate middleware interface per RPC shape and register through `DispatcherOptions` or configuration middleware lists.
- Modify protocol/telemetry tagging: `src/OmniRelay/Core/RequestLoggingScope.cs`, `src/OmniRelay/Transport/Grpc/GrpcTransportDiagnostics.cs`, and HTTP/gRPC inbounds.
- Add new outbounds/peer choosers: implement `ICustomOutboundSpec` or `ICustomPeerChooserSpec` and bind through configuration (`OmniRelay.Configuration`).

## Example patterns
- Register a unary procedure manually (see root `README.md`). Use a codec (e.g., `JsonCodec<TReq,TRes>`) to decode/encode bodies and return `Response<ReadOnlyMemory<byte>>`.
- Enabling HTTP/3 on gRPC outbound via config: in `outbounds:*:*:grpc:*:runtime`, set `enableHttp3: true`; optionally set `requestVersion: "3.0"` and `versionPolicy: "request-version-or-higher"`.
- Ensuring protocol metadata appears:
  - HTTP: server sets `Rpc-Protocol` header; client reads it into `ResponseMeta.Headers`.
  - gRPC: `GrpcMetadataAdapter` and interceptors propagate `rpc.protocol`.

## Key files/directories
- Dispatcher and middleware: `src/OmniRelay/Dispatcher/*`, `src/OmniRelay/Core/Middleware/*`
- HTTP transport: `src/OmniRelay/Transport/Http/*`
- gRPC transport: `src/OmniRelay/Transport/Grpc/*`
- Configuration binder & models: `src/OmniRelay.Configuration/*`
- Protobuf codegen: `src/OmniRelay.Codegen.Protobuf*`
- Tests (good examples): `tests/OmniRelay.Tests/Transport/Http/*`, `tests/OmniRelay.Tests/Transport/Grpc/*`

## Common troubleshooting
- If QUIC/HTTP/3 tests fail: Ensure your OS has MsQuic support and TLS 1.3 certificates are valid
- If gRPC tests fail on loopback: Check that HTTP/2 is enabled and ports aren't blocked
- For configuration validation issues: Use `omnirelay config validate` to check syntax and bindings
- If middleware isn't being applied: Verify it's added to the correct RPC shape's middleware list (unary, oneway, streaming)
- For protobuf codegen issues: Ensure `protoc` is installed and the `.proto` files follow gRPC service conventions

If parts of this guide are unclear or you spot divergent patterns (e.g., a new transport or codec), please highlight where you found them so we can refine these instructions.
