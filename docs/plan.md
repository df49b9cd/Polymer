# YARPC‑Go: Architecture and C# Port Plan

-----------------------------------------------------

> This document explains how `yarpc-go` works and provides a detailed, **Codex‑ready** implementation plan to build an equivalent in **C#/.NET**.

## Table of Contents

1. [What YARPC Is](#what-yarpc-is)
2. [Core Building Blocks](#core-building-blocks)
3. [C# Port: Scope & Milestones](#c-port-scope--milestones)
4. [Project Layout (Namespaces & Packages)](#project-layout-namespaces--packages)
5. [API Mapping (Go → C#)](#api-mapping-go--c)
6. [Wire-Up & Lifecycle](#wire-up--lifecycle)
7. [Transport Specifics](#transport-specifics)
8. [Encodings (JSON, Protobuf, Thrift)](#encodings-json-protobuf-thrift)
9. [Peer & Load-Balancing](#peer--load-balancing)
10. [Error Model](#error-model)
11. [Middleware & Observability](#middleware--observability)
12. [Configuration](#configuration)
13. [Interop & Testing](#interop--testing)
14. [Step-by-Step Codex Prompts](#step-by-step-codex-prompts)
15. [Design Nuances & Rationale](#design-nuances--rationale)
16. [Lightweight Usage Sketch (C#)](#lightweight-usage-sketch-c)
17. [Risks & Tradeoffs](#risks--tradeoffs)
18. [References (Key Points)](#references-key-points)

## What YARPC Is

**YARPC** is Uber’s RPC toolkit that cleanly separates concerns into **encodings**, **transports**, and **peer choosers**, allowing services to switch wire protocols and load‑balancing strategies without changing handler or call-site code. It provides reference transports (HTTP/1.1, gRPC, TChannel) and encodings (raw, JSON, Thrift, Protobuf). Polymer focuses on HTTP/1.1 and gRPC parity; TChannel is acknowledged historically but intentionally out of scope for the current effort. A central **Dispatcher** wires inbounds, outbounds, routing, and middleware, and manages lifecycle.

> **Status example:** latest release (at time of research): **v1.81.0** (Sep 15, 2025).

**Notable characteristics:**

- Pluggable middleware stacks across unary, oneway, and stream RPC types.
- Unified status/error model with cross-transport mappings.
- Observability baked in via tracing, metrics, and introspection endpoints.
- Configuration-driven bootstrap through `yarpcconfig` for consistent deployment.

## Core Building Blocks

### 1) Dispatcher

* Application façade that holds service name, inbounds/outbounds, router, and global middleware.

* Lifecycle: `Start()` / `Stop()`, with phased start/stop to bring up transports incrementally.

* Exposes `ClientConfig` for outbound selection and supports `Register`ing procedures/handlers.

* Supplies `Introspect()` for debug/observability.

### 2) Transports & RPC Types

* **Transports:** HTTP/1.1 and gRPC are first-class; focus on these transports and peer management.

* **RPC Types:**

  * **Unary** (req/resp): all transports.

  * **Oneway** (fire‑and‑forget): HTTP supports this.

  * **Streaming:** gRPC supports server/client/bidi streaming.

* Each transport exposes **inbounds** (servers) and **outbounds** (clients) behind a common API so higher‑level code is protocol‑agnostic.

### 3) Encodings

* **JSON**: typed request/response helpers.

* **Thrift**: via ThriftRW + the YARPC plugin.

* **Protobuf**: via `protoc` plugin(s), with v2 flow also supported.

### 4) Peer Management & Load Balancing

* Abstracts **peers** (remote endpoints) and **choosers** (selection strategies).

* Built‑ins: single peer, **round robin**, **fewest pending** (pending‑heap), two‑random‑choices, etc.

* Works with host:port identifiers; choosers are lifecycle‑bound.

### 5) Configuration System

* `yarpcconfig` reads YAML/Map to **build a Dispatcher** via registered TransportSpecs & PeerList specs.

* Typical keys: `inbounds`, `outbounds`, `transports`, `logging`.

* Define per‑service outbounds for `unary` and/or `oneway`.

### 6) Middleware

* Separate stacks for **inbound**/**outbound** and for **unary**, **oneway**, and **stream**.

* Combinators build ordered pipelines. Common uses: tracing, logging, rate‑limiting, retries.

### 7) Error Model

* Rich status type with canonical **codes** (e.g., `InvalidArgument`, `DeadlineExceeded`, `Unavailable`).

* Helpers: `FromError`, `IsStatus`, and client/server fault classification.

### 8) Tracing & Observability

* OpenTracing/OpenTelemetry helpers at transport level (span creation, error tagging).

* Introspection APIs and optional debug endpoints.

### 9) Lifecycle & Transport Composition

* Outbounds may wrap multiple underlying transports (e.g., for **traffic shadowing/migrations**).

* Dispatcher orchestrates start/stop across components.

### 10) Ecosystem Tools

* **yab** CLI issues Thrift/Protobuf requests over TChannel/HTTP/gRPC; handy for local testing and benchmarks (Polymer will concentrate on HTTP/gRPC compatibility).

* * *

C# Port: Scope & Milestones
---------------------------

**Objective:** Reach feature parity with `yarpc-go` for modern transports (HTTP, gRPC) while respecting .NET idioms and tooling.

| Phase | Focus | Key Deliverables | Acceptance Criteria | Notes |
| --- | --- | --- | --- | --- |
| 0. Foundation | Shared primitives | `Polymer.Core` abstractions layered on Hugo `Result`, `Error`, and middleware contracts | Unit tests cover request/response envelopes, adapter helpers, codec interface | Confirms Hugo primitives satisfy YARPC semantics without duplication |
| 1. MVP Transports | HTTP & gRPC pipelines | Dispatcher hosted on Generic Host, HTTP unary/oneway, gRPC unary/streaming, JSON & Protobuf codecs | Sample echo + key-value services run locally over both transports | Grpc.Net.Client requires .NET 6+; reuse HttpClientFactory; surface responses as Hugo `Result<T>` |
| 2. Platform Features | Operational parity | Peer chooser library using Hugo channels/task queues, declarative configuration builder, OpenTelemetry middleware via `GoDiagnostics`, error mapping, introspection endpoints | Config-driven app spins up with round-robin peers; telemetry captured in OTLP exporter | Align metrics/events with Uber conventions while reusing Hugo instrumentation |
| 3. Tooling & Interop | Developer productivity | `protoc-gen-yarpc-csharp`, integration tests vs `yarpc-go`, `yab` scenarios, benchmarking harness | Generated code passes interop suite; latency regression baseline documented | Extend to Thrift once Protobuf path is stable; validate Hugo adapters against Go parity |

**Deferred/Out of Scope (initially):**

- Focus on HTTP/1.1 and gRPC transports; TChannel is out of scope for the parity effort.
- Advanced peer discovery adapters (Consul/Eureka); plan to stub interfaces so they can be added by adopters.
- Language-specific code generators beyond Protobuf (Thrift planned once MVP stabilizes).
- Replacing Hugo primitives; the plan assumes Hugo remains the source for result, error, diagnostics, and concurrency helpers with thin YARPC adapters on top.

* * *

Project Layout (Namespaces & Packages)
--------------------------------------

| Directory | Assembly / Namespace | Purpose |
| --- | --- | --- |
| `src/Polymer/Core` | `Polymer.Core` (namespace) | Core abstractions within the consolidated `Polymer` project: dispatcher, metadata envelopes, codecs, middleware contracts, transport contracts. |
| `src/Polymer/Transport/Http` | `Polymer.Transport.Http` | ASP.NET Core inbound adapter, HttpClient-based outbound, oneway helpers, HTTP-specific middleware defaults. |
| `src/Polymer/Transport/Grpc` | `Polymer.Transport.Grpc` | gRPC inbound/outbound, metadata translators, streaming utilities, logging/metrics interceptors. |
| `src/Polymer/Configuration` | `Polymer.Configuration` | (Planned) Declarative config loader, DI extensions, transport/peer registries, validation logic. |
| `src/Polymer/Codegen/Protobuf` | `protoc-gen-polymer-csharp` | (Planned) Protobuf plugin emitting service adapters, client facades, and codec glue code. |
| `samples/KeyValueService` | `KeyValueService` | Demonstrates multi-transport dispatcher, config-driven wiring, OpenTelemetry integration. |
| `samples/PingClient` | `PingClient` | Console client exercising unary, oneway, and streaming calls for manual verification. |
| `tests/<ProjectName>.Tests` | Mirrors each library | xUnit-based unit tests, transport/interop harnesses, property tests for peer choosers. |

**Solution structure:** `Polymer.sln` at repo root; use `Directory.Build.props`/`Directory.Build.targets` to centralize analyzers, nullable context, and packaging metadata. Favor `InternalsVisibleTo` only for associated test assemblies.

**Dependencies:** treat Hugo packages (`Hugo`, `Hugo.Diagnostics.OpenTelemetry`, `Hugo.Go`) as external NuGet dependencies the core project references for result pipelines, diagnostics, and concurrency primitives.

* * *

API Mapping (Go → C#)
---------------------

| Go Concept | C# Equivalent |
| --- | --- |
| `yarpc.Dispatcher` | `Dispatcher` (inbounds/outbounds/router/middleware lifecycle) |
| `transport.UnaryOutbound/OnewayOutbound/StreamOutbound` | `IUnaryOutbound` / `IOnewayOutbound` / `IStreamOutbound` |
| `transport.Inbound` + handler specs | `IUnaryInbound` / `IOnewayInbound` / `IStreamInbound` |
| `peer.Chooser` + lists (round‑robin, pending‑heap) | `IPeerChooser` + `RoundRobinPeerList`, `PendingHeapPeerList` |
| `yarpcconfig` | `PolymerConfiguration` (YAML/JSON → Dispatcher) |
| `encoding/json`, `/thrift`, `/protobuf` | `JsonCodec`, `ProtobufCodec` (Thrift later) |
| `yarpcerrors` | `PolymerException` + `PolymerStatusCode` enum |
| Middleware packages | Inbound/Outbound interfaces + ordered pipeline combinators |
| Hugo `Error`/`Result<T>` | Backing transport error/result semantics; adapters attach YARPC status metadata to Hugo errors |

* * *

Wire‑Up & Lifecycle
-------------------

* Implement `Dispatcher` as an `IHostedService` plugged into the .NET Generic Host so transports can join the shared lifetime, logging, and DI container.

* **Start sequence:** initialize codecs and middleware → start transports → warm up outbounds (peer resolution, connection pools) → register inbounds and publish router table → emit readiness events.

* **Stop sequence:** mark dispatcher unavailable → stop accepting new inbound requests → drain in-flight calls with deadline honoring → dispose outbounds/transports → emit `Stopped` diagnostics.

* Router maps procedures (encoding-specific names) to handlers; `Introspect()` surfaces registered procedures, middleware chains, peer chooser state, and transport health snapshots.

* Provide helper extensions (`AddPolymerDispatcher`, `UsePolymer`) so ASP.NET Core or worker services can register dispatcher components declaratively in `Program.cs`.

* Bootstrap Hugo diagnostics (`GoDiagnostics.Configure(...)`) as part of dispatcher startup so transport/middleware metrics flow into the shared telemetry backends without bespoke instrumentation.

* * *

Transport Specifics
-------------------

### HTTP/1.1

* **Inbound:** ASP.NET Core middleware translating HTTP to `IRequest<byte[]>`; decode headers into `RequestMeta`; invoke handler; respond or **202** for oneway.

* **Unary Outbound:**`HttpClient` wrapper; `ICodec` encodes/decodes; propagate headers/meta; enforce deadlines/cancellation.

* **Oneway Outbound:** send without awaiting body; return Ack.

* **Headers & baggage:** normalize YARPC application headers to canonical HTTP names (`Rpc-Caller`, `Rpc-Service`, etc.) and support multi-valued headers.

* **Timeouts & retries:** map `RequestMeta.TTL` onto `CancellationToken`/`Timeout` and ensure retry middleware cooperates with `HttpClientFactory` policies.

* **Observability:** emit OpenTelemetry spans, metrics (request duration, inflight count), and structured logs via middleware hooks.

* **Result surface:** translate handler responses to Hugo `Result<T>` so downstream middleware (retries, fallbacks) stays declarative.

### gRPC

* **Inbound:** gRPC service delegating to YARPC handler façade; normalize metadata via headers/trailers.

* **Outbounds:**`GrpcChannel` + client interceptors wrapping `IUnaryOutbound`&`IStreamOutbound`.

* **Status Mapping:** gRPC `StatusCode` ↔ `PolymerStatusCode`.

* **Streaming support:** expose async enumerable APIs for server/client/bidi streams; ensure back-pressure ties into YARPC middleware.
* **Docs:** `docs/reference/streaming.md` captures handler registration patterns, client helpers, and completion semantics across server, client, and duplex streaming.
* **Peer management:** `GrpcOutbound` accepts multiple peers, composes configurable `IPeerChooser` implementations, and applies `PeerCircuitBreaker` safeguards; diagnostics surface per-peer health via `GrpcOutboundSnapshot`.
* **Deadlines:** TTL/absolute deadlines on `RequestMeta` translate into gRPC `CallOptions` deadlines, with tests asserting `DeadlineExceeded` mapping.

* **Metadata bridging:** serialize YARPC headers into gRPC metadata (binary-safe) and convert trailers back into response metadata.

* **Channel management:** integrate with .NET `GrpcChannelOptions` (connection pooling, TLS) and allow DI-based channel reuse per service.

* **Result surface:** ensure streaming and unary paths surface `Result<T>`/`Error` so gRPC status mapping rides on Hugo adapters.

* * *

Encodings (JSON, Protobuf, Thrift)
----------------------------------

* **JSON:**`ICodec<TReq,TRes>` using `System.Text.Json`; pluggable.

* **Protobuf:** Thin adapters with `Google.Protobuf`; provide `protoc-gen-yarpc-csharp` to generate typed clients/servers over YARPC Core.

* **Thrift (later):** integrate Apache Thrift C# with a small YARPC adapter (optional).

* **Negotiation:** allow dispatcher registration to specify default codec per procedure while honouring explicit call-site overrides.

* **Versioning:** document approach for payload evolution (backward compatibility, optional fields) and surface validation helpers for JSON schema drift.

* **Compression:** expose opt-in content encoding (gzip) consistent with YARPC-Go behaviour for larger payloads.

* * *

Peer & Load‑Balancing
---------------------

* **Peer identity:** host:port (+ scheme for HTTP).

* **Choosers:**

  * `RoundRobinPeerList` (cycle over healthy peers). *(Implemented via `Polymer.Core.Peers.RoundRobinPeerChooser`; gRPC outbound now leases peers with inflight accounting.)*

  * `PendingHeapPeerList` (fewest in-flight; tie-break RR/random). *(Covered by `FewestPendingPeerChooser`; extend to heap-based implementation for large peer sets.)*

  * `TwoRandomChoices` (power-of-two). *(Implemented via `TwoRandomPeerChooser`.)*

* **Health:** per-peer inflight counters, error backoff, basic circuit-breaking hooks. *(Completed: `PeerCircuitBreaker` now supports exponential backoff, half-open probing limits, and success thresholds, keeping unhealthy peers quarantined until they recover.)*

* **Discovery hooks:** design `IPeerTransport`/`IPeerDirectory` to plug static lists, DNS SRV, or future service discovery without altering core choosers.

* **Metrics:** `PeerMetrics` now emits success/failure counters and inflight gauges per peer, enabling dashboards and alerting.

* **Implementation note:** build chooser internals atop Hugo channel builders, wait groups, and result orchestration helpers to align concurrency behavior with existing libraries.

* * *

Error Model
-----------

* `PolymerStatusCode` enum and `PolymerException` type.

* Helpers: `FromException`, `IsStatus`, `GetFaultType` (client/server).

* Mapping: gRPC codes ↔ YARPC codes; HTTP status/headers → YARPC codes.

* **.NET integration:** provide exception filters/middleware so ASP.NET Core controllers can translate thrown exceptions into `PolymerException` consistently.

* **Retry semantics:** publish guidance on which status codes are retryable and ensure middleware respects them.

* **Hugo alignment:** adapt YARPC status enums into Hugo `Error.Code` metadata so existing Hugo-aware tooling (result pipelines, diagnostics) understands transport failures.

* * *

Middleware & Observability
--------------------------

* Middleware interfaces for unary/oneway/stream (inbound & outbound) + combinators to build ordered pipelines.

* **Stock middleware:** logging, tracing (OpenTelemetry), metrics, deadline enforcement, panic/exception recovery (convert to `PolymerException`).

* **Extensibility:** support per-procedure middleware overrides and attribute-based registration for generated clients.

* **Telemetry:** adopt OpenTelemetry semantic conventions (rpc.system=yarpc, rpc.service, rpc.method) and export metrics via `MeterProvider`.

* **Context propagation:** ensure tracing context and baggage propagate across transport boundaries using `Activity` + W3C headers.

* **Hugo diagnostics:** plug `Hugo.Diagnostics.OpenTelemetry` middleware where possible so span/metric emission remains consistent with other Hugo-based services.

* * *

Configuration
-------------

* **Schema:**`inbounds`, `outbounds`, `transports`, `logging`, `peers` (static lists or lists by strategy).

* **Binder:** YAML/JSON → typed models; register TransportSpecs & PeerListSpecs; build a ready `Dispatcher`.

* **Validation:** enforce required fields (service name, at least one inbound/outbound), surface helpful diagnostics, and support environment overrides (appsettings + environment variables).

* **Sample YAML:**

```yaml
yarpc:
  service: keyvalue
  inbounds:
    - http:
        address: "http://0.0.0.0:8080"
        procedures:
          - encoding: json
            name: keyvalue::get
  outbounds:
    keyvalue:
      unary:
        http:
          url: "http://keyvalue.local:8080"
          peerList:
            roundRobin:
              peers:
                - "http://keyvalue-1:8080"
                - "http://keyvalue-2:8080"
  logging:
    level: Information
```

* **Hot reload:** optional file watcher that rebuilds dispatcher configuration without process restart (behind feature flag).

* * *

Interop & Testing
-----------------

* **Conformance:** routing, metadata propagation, deadlines, error mapping.

* **Property:** chooser fairness & least‑pending behavior.

* **Interop:** call a real `yarpc-go` sample over HTTP/gRPC; verify metadata, codes, streaming.

* **Benchmark:**`yab` scripts for throughput/latency comparisons.

* **CI strategy:** GitHub Actions workflow running unit tests, transport integration suites (HTTP & gRPC), and interop smoke tests against dockerized `yarpc-go`.

* **Load testing:** capture baseline QPS and latency percentiles; document target SLOs and regressions thresholds.

* **Regression harness:** include golden-record tests for generated Protobuf code to catch breaking changes.

* * *

Step‑by‑Step Codex Prompts
--------------------------

Each step includes _Done when…_ acceptance criteria.

> **Status:** Steps 1‑3 are implemented in the consolidated `Polymer` project; the prompts remain for historical traceability and future enhancements.

**Workflow guidance:**

- Target one library per pull request to keep diffs reviewable; wire minimal samples/tests alongside the feature.
- Prefer TDD for core primitives and peer choosers; integration tests for transport bindings.
- Update documentation (`README`, `samples`) as part of each step so Codex always has fresh context.

### 1) Core Primitives

**Prompt:** Create `Polymer.Core` (wrapping Hugo building blocks) with:

* `RequestMeta`, `ResponseMeta`, `IRequest<T>`, `IResponse<T>`.

* Interfaces: `IUnaryOutbound`, `IOnewayOutbound`, `IStreamOutbound`, `IUnaryInbound`, `IOnewayInbound`, `IStreamInbound`, `ITransport`, `ILifecycle`.

* `ICodec<TReq,TRes>`, `JsonCodec<TReq,TRes>`.

* Errors: `PolymerStatusCode`, `PolymerException`, helpers `FromException`, `IsStatus`, `GetFaultType`.

* Middleware interfaces and `Compose(...)` combinators.

* Hugo adapters translating `Hugo.Error`/`Result<T>` into the Polymer-specific surface (status codes, headers, metadata).

**Done when:** Library builds; can throw/catch `PolymerException` with a code and convert to/from Hugo `Error`.

* * *

### 2) Dispatcher & Router

**Prompt:** Implement `Dispatcher`:

* Holds service name, inbounds/outbounds, router, middleware stacks.

* Methods: `Register(ProcedureSpec)`, `StartAsync`, `StopAsync`, `ClientConfig(...)`.

* Phased start/stop; `Introspect()` returns procedures/transports/middleware.

* Lifecycle coordination should reuse Hugo wait groups/select utilities to manage concurrent transport startup/shutdown.

**Done when:** Minimal sample starts/stops a dispatcher with no routes.

* * *

### 3) HTTP Transport (Unary + Oneway)

**Prompt:** Build `Polymer.Transport.Http`:

* **Inbound:** ASP.NET Core middleware → `IRequest<byte[]>` → handler; 200 for unary, **202** for oneway.

* **Unary Outbound:**`HttpClient` wrapper with `ICodec`, headers/meta propagation, deadlines/cancellation.

* **Oneway Outbound:** fire‑and‑forget; returns Ack.

* Map transport responses to Hugo `Result<T>` so middleware can reuse retry/fallback helpers.

**Done when:**`HelloService/Get` accepts JSON over HTTP and returns JSON; oneway returns 202.

* * *

### 4) gRPC Transport (Unary + Streaming)

**Prompt:** Build `Polymer.Transport.Grpc`:

* **Inbound:** generic gRPC service delegating to YARPC handlers; header/trailer normalization.

* **Outbounds:**`Grpc.Net.Client` wrappers implementing `IUnaryOutbound` and `IStreamOutbound`; client interceptors.

* **Status Mapping:** gRPC ↔ YARPC.

* Leverage Hugo `Result<T>` for unary/streaming responses and convert gRPC status/trailers into Hugo error metadata.

**Done when:** Streaming echo works; unary parity with HTTP sample.

* * *

### 5) Middleware Pipeline

**Prompt:** Implement middleware combinators and stock middleware:

* Logging, tracing (OpenTelemetry), metrics, deadline, panic/exception recovery.

**Done when:** Middleware attached to dispatcher produces logs/metrics/traces.

*Implementation tip:* compose middleware via Hugo `Functional` extensions where possible to reuse existing retry/timeout primitives.

**Status:** `RpcLoggingMiddleware` handles structured logs, `RpcTracingMiddleware` emits `ActivitySource` spans, `RpcMetricsMiddleware` publishes request counters/latency histograms, `DeadlineMiddleware` enforces TTL/deadline metadata, `PanicRecoveryMiddleware` maps unhandled exceptions to canonical failures, `RetryMiddleware` adds Hugo-powered retry/backoff for outbound calls, and `RateLimitingMiddleware` applies concurrency-based controls with `ResourceExhausted` signaling. Streaming-specific context is now provided via `StreamCallContext` / `DuplexStreamCallContext` (exposed on `IStreamCall` and `IDuplexStreamCall`) so middleware can inspect message counts and completion reasons without transport coupling. Circuit breaking remains outstanding.

* * *

### 6) Peer Chooser & Load Balancing

**Prompt:** Add `Polymer.Core.Peer`:

* `IPeer`, `IPeerChooser`, `PeerStatus`, inflight accounting.

* `RoundRobinPeerList`, `PendingHeapPeerList`.

* Outbound → `AcquirePeer()` per request with `onFinish(error)` hooks.

**Done when:** Unit tests verify fairness and least‑pending behavior.

*Implementation tip:* use Hugo channel builders / task queue primitives to coordinate peer leases and inflight tracking.

**Status:** `PeerMetrics` now tracks lease acquisitions, releases, rejections, and retry scheduling; choosers emit busy/exhausted counters, and `RetryMiddleware` reports scheduled/exhausted retries. This keeps peer health visible to observability tooling and aligns retry behaviour with peer state.

* * *

### 7) Configuration System

**Prompt:** Create `Polymer.Configuration`:

* Models for `Inbounds`, `Outbounds`, `Transports`, `Logging`, `Peers`.

* DI registration of `TransportSpec`/`PeerListSpec`.

* YAML/JSON binder; builder → `Dispatcher`.

**Done when:** YAML can spin up HTTP inbound on `:8080`, HTTP unary+oneway outbound to service `keyvalue` with RR peers, and logging levels.

* * *

### 8) Protobuf Codegen

**Prompt:** Implement `protoc-gen-yarpc-csharp`:

* Generate typed clients (wrap `IUnaryOutbound`/`IStreamOutbound`) and server adapters (register with `Dispatcher`).

* Use `Google.Protobuf` for (de)serialization.

**Done when:** Generated code round‑trips over gRPC; optional Protobuf‑over‑HTTP via codec.

* * *

### 9) Error Parity & Mappings

**Prompt:** Provide bi‑directional mappings:

* gRPC `StatusCode` ↔ `PolymerStatusCode`; HTTP status classes → YARPC codes.

* Verify `FromException`, `IsStatus`, `GetFaultType` behavior in middleware.

**Done when:** Conformance tests pass for code propagation.

* * *

### 10) Introspection & Tooling

**Prompt:** Expose `/polymer/introspect`:

* JSON view of procedures, inbounds/outbounds, peer chooser state, middleware chains.

**Status:** `HttpInbound` serves `/polymer/introspect`, returning dispatcher status, lifecycle components, middleware, grouped procedure listings (with streaming metadata defaults), and outbound diagnostics pulled from transport snapshots (`GrpcOutboundSnapshot`, `HttpOutboundSnapshot`, etc.). Remaining parity work is to layer in live peer health stats once choosers/backoff are implemented.

**Done when:** Endpoint reflects live state and updates dynamically.

* * *

### 11) Interop & Benchmarking

**Prompt:** Interop tests against a `yarpc-go` sample (HTTP/gRPC; unary/stream). Provide `yab` commands for benchmarks.

**Status:** Initial `yab` harness and helper script live under `tests/Polymer.YabInterop`; extend to full yarpc-go interop matrix and benchmarking tooling.

**Done when:** Tests pass; `yab` successfully exercises the C# server.

* * *

Design Nuances & Rationale
--------------------------

* **Transport‑agnostic APIs** keep handlers/middleware reusable across HTTP and gRPC.

* **Oneway on HTTP** mirrors YARPC’s feature set and is practical for async fire‑and‑forget.

* **Streaming on gRPC** aligns with YARPC‑Go capabilities and platform strengths.

* **Peer choosers** start with RR and least‑pending to cover common balancing needs.

* **Error/Tracing parity** ensures consistent diagnostics across transports.

* **Declarative config** enables ops‑friendly deployment and easy migration/shadowing.

* **Hosted service integration** leverages .NET Generic Host for unified lifecycle and resiliency (graceful shutdown, health checks).

* **Generator-first approach** reduces manual boilerplate and encourages schema-driven development for new services.

* **Hugo leverage** keeps error/result, diagnostics, and concurrency behavior consistent with existing platform libraries while reducing reimplementation risk.

* * *

Lightweight Usage Sketch (C#)
-----------------------------

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polymer.Core;
using Polymer.Transport.Http;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddLogging();
builder.Services.AddPolymerDispatcher(dispatcher =>
{
    dispatcher.SetServiceName("keyvalue");
    dispatcher.AddInbound(HttpInbound.Create("http://0.0.0.0:8080"));
    dispatcher.AddOutbound("keyvalue", outbound =>
    {
        outbound.UseHttp(http =>
        {
            http.BaseAddress = new Uri("http://keyvalue:8080");
        });
    });
    dispatcher.RegisterJsonUnary<GetRequest, GetResponse>("keyvalue::get", async (ctx, request) =>
    {
        // business logic; request.Context exposes headers/meta
        return new GetResponse { Value = await store.GetAsync(request.Key, ctx.CancellationToken) };
    });
});

var app = builder.Build();
await app.RunAsync();
```

**Client sample:**

```csharp
var dispatcher = host.Services.GetRequiredService<Dispatcher>();
var client = dispatcher.CreateJsonClient<GetRequest, GetResponse>("keyvalue");

var response = await client.CallAsync(new GetRequest { Key = "foo" }, cancellationToken);
Console.WriteLine($"value = {response.Value}");
```

The sketch assumes helper extensions (`AddPolymerDispatcher`, `RegisterJsonUnary`, `CreateJsonClient`) introduced in the plan’s earlier steps. Replace with equivalent wiring if naming changes.

* * *

Risks & Tradeoffs
-----------------


* **Wire‑level exactness:** pay attention to header names and metadata interop; validate with conformance tests.

* **Retries:** prefer middleware‑driven retries with jittered backoff and idempotency guards; align with gRPC guidance on retry semantics.

* **Performance parity:** .NET HttpClient and gRPC stacks differ from Go implementations; monitor GC pressure and thread-pool usage during benchmarks.

* **Config drift:** declarative configuration introduces validation risk; invest in schema validation and tooling to avoid runtime surprises.

* **Codegen maintenance:** protoc plugin needs CI coverage to stay in sync with upstream Google.Protobuf updates.

* **Hugo alignment:** upstream Hugo changes could affect adapters; lock versions and monitor release notes for breaking diagnostics/result semantics.

* * *

References (Key Points)
-----------------------

* `yarpc-go` repository (architecture docs, transports, encodings, middleware, errors) — baseline for behavior parity and API inspiration.

* `yarpcconfig` reference examples — guides structure of declarative configuration and transport specs.

* `yarpcerrors` package — authoritative mapping of canonical status codes and helpers.

* `yarpc-go/peer` packages — round-robin, peer heap, and chooser interfaces used to shape the C# peer model.

* `grpc-dotnet` documentation — implementation details for gRPC services, channels, interceptors, and streaming in .NET.

* OpenTelemetry .NET (`System.Diagnostics.Activity`, `OpenTelemetry.Trace`, `OpenTelemetry.Metrics`) — tracing/metrics primitives used by middleware.

* `System.Text.Json` advanced serialization docs — for high-performance JSON codecs (source generators, buffer pooling).

* `yab` CLI usage guides — interop and benchmarking workflows to validate wire compatibility.
* Hugo reference docs (`docs/reference/hugo-api-reference.md`, `docs/reference/concurrency-primitives.md`, `docs/reference/result-pipelines.md`) — canonical guidance for result/error, diagnostics, and concurrency adapters used throughout the port.
