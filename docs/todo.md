# OmniRelay Parity TODO

Comprehensive backlog tracking the remaining work needed to reach feature parity with `yarpc-go`. Every item is broken into actionable sub-tasks so the team can triage and implement incrementally. When a task is marked *completed*, its subtasks are preserved for historical reference.

## 1. Transports

- ~~**HTTP Oneway Completion (Phase 3)**~~ *(completed)*

- **gRPC Oneway & Streaming (Phase 4)**
  - ~~Implement unary→oneway adaptation so gRPC unary handlers can act as oneway calls.~~
    - ~~Update dispatcher registration for `OnewayProcedureSpec`~~
    - ~~Teach gRPC inbound to call oneway handlers and surface ack trailers~~
    - ~~Add client helper (`CreateOnewayClient`) + outbound mapping~~
    - ~~Add integration tests validating ack + metadata propagation~~
  - ~~Server streaming (request unary → response stream).~~
    - ~~Add server-stream call abstraction (channels + completion)~~
    - ~~Wire dispatcher `InvokeStreamAsync` and HTTP/gRPC providers~~
    - ~~Implement outbound pipeline for server streaming~~
    - ~~Add codec integration + SSE example tests~~
  - **Client streaming (request stream → unary response)**
    - ~~Extend dispatcher with `InvokeClientStreamAsync` returning a writable request channel and providing a mechanism to deliver the unary response once completed.~~ *(completed)*
    - ~~Modify gRPC inbound provider to bridge `IAsyncStreamReader<byte[]>` into the dispatcher channel, honoring cancellation, deadlines, and backpressure (await `WaitToReadAsync` before `ReadAsync`).~~ *(completed)*
    - ~~Enhance `GrpcOutbound` with `AsyncClientStreamingCall`, providing a client-streaming facade that:~~ *(completed)*
      - ~~Encodes request chunks via the configured codec.~~
      - ~~Pushes frames respecting `WriteOptions`/backpressure (await write completions, apply cancellation).~~
      - ~~Receives unary response + metadata, decoding into `Response<T>`.~~
    - ~~Expand `StreamClient<TReq,TRes>` (or new client type) to expose a high-level API (async writer + awaited response).~~ *(completed)*
    - ~~Add middleware hooks for outbound/inbound client streams (typed context containing channels).~~ *(completed)*
    - ~~Tests:~~ *(completed)*
      - ~~Streaming success (multiple frames, aggregated response).~~
      - ~~Cancellation from client before completion.~~
      - ~~Deadline expiry, verifying status mapping.~~
      - ~~Large payload handling and chunked writes.~~
  - **Bidirectional streaming (request stream ↔ response stream)**
    - ~~Introduce dispatcher duplex stream abstraction (read/write channels with completion signaling and error propagation).~~ *(completed)*
    - ~~Extend gRPC inbound to pipe client messages into dispatcher channel while writing outbound responses via `IServerStreamWriter<byte[]>`; ensure:~~ *(completed)*
      - ~~Concurrent read/write with cancellation tokens.~~
      - ~~Completed/error states propagate to both sides.~~
      - ~~Metadata headers/trailers reflect final status.~~
    - ~~Update outbound API to expose duplex stream client:~~ *(completed)*
      - ~~Provide read/write channels or high-level enumerables.~~
      - ~~Encode/decode per-message using codecs.~~
      - ~~Manage backpressure (await writes, buffer limits).~~
    - Middleware:
      - ~~Ensure global + per-procedure middleware can observe both inbound/outbound flows.~~ *(completed)*
      - ~~Provide context objects for stream state (message count, completion reason).~~ *(completed via `StreamCallContext` / `DuplexStreamCallContext` surfaced on all streaming call contracts with message counters and completion metadata; covered by `HttpStreamCallTests` and updated `DuplexStreamCallTests`.)*
    - Tests:
      - ~~Add gRPC duplex integration coverage mirroring the HTTP echo scenario (currently only `HttpTransportTests.DuplexStreaming_OverHttpWebSocket` exercises this path).~~ *(completed via `GrpcTransportTests.DuplexStreaming_OverGrpcTransport`)
      - ~~Automate gRPC flow-control scenario (server slower than client).~~ *(completed via `GrpcTransportTests.DuplexStreaming_FlowControl_ServerSlow`)
      - ~~Verify cancellation initiated by server and by client over gRPC.~~ *(completed via `GrpcTransportTests.DuplexStreaming_ServerCancellationPropagatesToClient` and `GrpcTransportTests.DuplexStreaming_ClientCancellationPropagatesToServer`)
      - ~~Validate metadata propagation (custom headers/trailers) for gRPC duplex streams.~~ *(completed via `GrpcTransportTests.DuplexStreaming_PropagatesMetadata`)
  - **Shared streaming tasks**
    - ~~Update dispatcher introspection to list available stream procedures and status (unary, client, server, bidi).~~ *(completed)*
    - ~~Document public APIs (how to build client streaming/bidi handlers).~~ *(documented in `docs/reference/streaming.md` with server, client, and duplex guidance.)*
    - ~~Expand gRPC outbound/inbound error handling to surface canonical codes for streaming faults.~~ *(completed)*
    - **gRPC transport test gaps (parity audit)**
      - ~~Cover duplex/bidirectional streaming happy path, cancellation, and flow-control scenarios.~~ *(completed via `GrpcTransportTests.DuplexStreaming_OverGrpcTransport`, `_ServerCancellationPropagatesToClient`, `_ClientCancellationPropagatesToServer`, and `_FlowControl_ServerSlow`.)*
      - ~~Validate request/response metadata propagation (`rpc-*` headers, custom headers, TTL/deadline) across unary and streaming RPCs.~~ *(covered by `GrpcTransportTests.ServerStreaming_PropagatesMetadataAndHeaders` and streaming meta assertions.)*
      - ~~Assert response trailers carry `polymer-encoding`, status, and error metadata for success & failure cases.~~ *(completed via `GrpcTransportTests.GrpcTransport_ResponseTrailers_SurfaceEncodingAndStatus` and `_SurfaceErrorMetadata`.)*
      - ~~Verify `GrpcStatusMapper` mappings for all canonical codes surfaced by unary and streaming transports.~~ *(covered by `GrpcTransportTests.GrpcStatusMapper_FromStatus_MapsExpected`/`_ToStatus_MapsExpected`.)*
      - ~~Exercise streaming edge conditions: server-initiated cancellation/error for server and client streams, partial writes, and mid-stream failures.~~ *(addressed through `GrpcTransportTests.ServerStreaming_ErrorMidStream_PropagatesToClient`, client stream cancellation/error tests, and duplex cancellation coverage.)*
      - ~~Add regression coverage once upstream enables forcing request/response compression so we can validate advertised `grpc-accept-encoding` behavior.~~ *(covered by `GrpcTransportTests.GrpcOutbound_CreateCallOptionsAddsAcceptEncoding` and `_PreservesExistingAcceptEncoding`, which assert negotiation headers and call option compression.)*
      - ~~Compression negotiation coverage: advertise supported compressors (`grpc-accept-encoding`) and verify request/response compression once upstream exposes the necessary hooks.~~ *(Outbound call options now populate `grpc-accept-encoding` metadata when `GrpcCompressionOptions.DefaultAlgorithm` is configured; inbound defaults validated via unit tests. Request compression will follow once the managed client exposes the required hook.)*

    - ~~Peer management & load balancing: GrpcOutbound binds to a single _address and opens one GrpcChannel with no peer chooser, retention, or backoff logic (src/OmniRelay/Transport/Grpc/GrpcOutbound.cs (lines 17-45)). YARPC-Go’s gRPC transport fronts multiple peers with choosers, dial-time backoff, and per-peer lifecycle management, so we currently lack parity on resiliency and multi-host routing.~~ *(completed: `GrpcOutbound` now accepts multiple addresses, leases peers through configurable `IPeerChooser` implementations, and integrates `PeerCircuitBreaker` options; validated by `GrpcTransportTests.GrpcOutbound_RoundRobinPeers_RecordSuccessAcrossEndpoints` and circuit-breaker tests.)*

    - ~~Observability & middleware: Added built-in logging/metrics interceptors (`GrpcClientLoggingInterceptor`, `GrpcServerLoggingInterceptor`), streaming message/duration metrics, and runtime hooks; still need integration with central telemetry config.~~ *(completed via `GrpcTelemetryOptions`, which wires client/server logging interceptors into `GrpcOutbound` and `GrpcInbound` by default; validated by `GrpcTransportTests.GrpcOutbound_TelemetryOptions_EnableClientLoggingInterceptor` and `.GrpcInbound_TelemetryOptions_RegistersServerLoggingInterceptor`.)*

    - ~~Security & connection tuning: The inbound config only forces HTTP/2 listeners and never exposes TLS, keepalive, or max message size knobs (src/OmniRelay/Transport/Grpc/GrpcInbound.cs (lines 60-71)); the outbound constructor similarly only tweaks the HTTP handler (src/OmniRelay/Transport/Grpc/GrpcOutbound.cs (lines 27-39)). YARPC-Go ships options for server/client TLS credentials, keepalive params, compressors, and header size limits, so our transport is missing that flexibility.~~ *(completed via `GrpcServerTlsOptions`, `GrpcClientTlsOptions`, runtime keepalive/message-size options, and `GrpcCompressionOptions`; request/response compression pipeline remains to be validated.)*
    
    - ~~Operational introspection: Our gRPC types implement only the OmniRelay transport interfaces (src/OmniRelay/Transport/Grpc/GrpcOutbound.cs (lines 15-25); src/OmniRelay/Transport/Grpc/GrpcInbound.cs (lines 17-42)) and expose no running-state, peer list, or metrics surfaces. YARPC-Go’s inbound/outbound implement introspection APIs and structured logging, so we lack comparable admin visibility.~~ *(completed via `IOutboundDiagnostic` snapshots (`GrpcOutboundSnapshot`, `GrpcPeerSummary`) consumed by the dispatcher introspection endpoint; see `GrpcTransportTests` diagnostics assertions and `HttpIntrospectionTests.IntrospectionEndpoint_ReportsDispatcherState`.)*
    
    - ~~Deadline/TTL enforcement & header hygiene: We serialize TTL/deadline information into metadata (src/OmniRelay/Transport/Grpc/GrpcMetadataAdapter.cs (lines 75-88)) but never convert it into gRPC deadlines in CallOptions, so the runtime won’t actually enforce them (src/OmniRelay/Transport/Grpc/GrpcOutbound.cs (lines 61-174)). YARPC-Go validates headers, propagates TTLs into contexts, and distinguishes application errors via metadata, which leaves us short on request semantics.~~ *(completed: `GrpcOutbound` maps TTL/deadline via `ResolveDeadline` when building `CallOptions`, and tests like `GrpcTransportTests.ClientStreaming_DeadlineExceededMapsStatus` verify deadline propagation and status mapping.)*


- ~~**Transport Middleware & Interceptors**~~ *(completed: HTTP outbound now composes transport-aware middleware via `HttpClientMiddlewareComposer` with logging sample (`HttpClientLoggingMiddleware`), gRPC client/server use composite interceptors with ordered registries, and regression coverage lives in `HttpOutboundMiddlewareTests` and `GrpcInterceptorPipelineTests`.)*

- ~~**Transport Lifecycle & Health**~~ *(completed: HTTP inbound now tracks active requests, returns `503` with `Retry-After` while draining, and exposes `/healthz` + `/readyz` backed by `DispatcherHealthEvaluator`; gRPC inbound gates new calls during shutdown, coordinates GOAWAY/drain waits, and publishes the standard health service via `GrpcTransportHealthService`. Coverage added in `HttpInboundLifecycleTests` and extended `GrpcTransportTests` for graceful vs forced stops and health transitions.)*

## 2. Encodings & Code Generation (Phase 8)

- **Raw/Binary Encoding**
  - ~~Create `RawCodec` (byte[] passthrough) with metadata enforcement.~~ *(implemented in `src/OmniRelay/Core/RawCodec.cs` with strict encoding checks and zero-copy reuse when possible.)*
  - ~~Ensure HTTP/gRPC transports propagate binary content-type headers correctly.~~ *(HTTP outbound now maps `raw` encoding to `application/octet-stream` and inbound normalizes ack/response headers; covered by `HttpDuplexTransportTests`.)*
  - ~~Add tests ensuring raw payloads bypass serialization.~~ *(covered by `tests/OmniRelay.Tests/Core/RawCodecTests.cs`, validating encode/decode pass-through and metadata enforcement.)*

- **Protobuf Support**
  - ~~Author `protoc-gen-polymer-csharp` plugin:~~ *(implemented in `src/OmniRelay.Codegen.Protobuf`; generator emits dispatcher registration helpers plus lazy C# clients and `ProtobufCodec` wiring. Usage documented in `docs/reference/codegen/protobuf.md`.)*
    - ~~Generate request/response DTOs, codecs, dispatcher registration stubs, client helpers.~~ *(Generated output lives under `tests/OmniRelay.Tests/Generated/TestService.OmniRelay.g.cs` with golden coverage.)*
    - ~~Provide MSBuild integration / tooling instructions.~~ *(Documented in `docs/reference/codegen/protobuf.md`, including `Grpc.Tools` pairing and `protoc` invocation.)*
  - ~~Support Protobuf over gRPC (native) and optional HTTP Protobuf:~~ *(Runtime `ProtobufCodec` gained media-type negotiation; HTTP metadata normalization extended so JSON/Protobuf interop works across transports.)*
    - ~~Handle content negotiation & media types.~~ *(HTTP inbounds/outbounds now normalize `application/x-protobuf` ↔ `protobuf`.)*
  - ~~Write codegen tests (golden outputs) and integration tests round-tripping messages across transports.~~ *(See `ProtobufCodeGeneratorTests` for golden coverage and `GeneratedServiceIntegrationTests` for HTTP and gRPC round-trips.)*
  - ~~Expose Roslyn incremental generator and MSBuild wiring sample.~~ *(Incremental generator lives in `src/OmniRelay.Codegen.Protobuf.Generator`; `tests/OmniRelay.Tests/Projects/ProtobufIncrementalSample` demonstrates referencing the analyzer with a descriptor set AdditionalFile.)*

- **Thrift Encoding**
  - Investigate options: port ThriftRW vs using Apache Thrift.
  - Implement codec translating between Thrift `TProtocol` payloads and `Result<T>`.
  - Generate server/client wrappers from IDL (with examples).
  - Add interop tests using YARPC fixtures.

- **JSON Enhancements**
  - ~~Allow custom `JsonSerializerOptions` via configuration (per-procedure overrides).~~ *(`DispatcherBuilder` now binds `encodings:json` profiles/registrations into strongly typed `JsonCodec` instances, wiring them into the dispatcher via `DispatcherOptions.Add*Codec`; validated by `OmniRelayConfigurationTests.AddOmniRelayDispatcher_ConfiguresJsonCodecs`.)*
  - ~~Add source-generated context support for performance.~~ *(`JsonCodec` accepts `JsonSerializerContext` metadata; configuration may reference generated contexts (see `EchoJsonContext`) ensuring serializers use statically generated type info.)*
  - ~~Optional: schema validation hooks leveraging JSON Schema.~~ *(`JsonCodec` gained optional request/response `JsonSchema` enforcement, triggered through configuration-provided schema paths; failures map to `InvalidArgument` with schema error metadata and are exercised in new unit tests.)*

- **Codec Registry**
  - ~~Introduce registry at dispatcher level mapping procedure → codec.~~ *(Added `CodecRegistry` with typed lookup/registration and memory-safe alias handling inside `Dispatcher`.)*
  - ~~Provide DI integration so transports autoselect codecs without manual wiring.~~ *(`DispatcherClientExtensions` gained registry-backed overloads; `AddOmniRelayDispatcher` now exposes the shared registry via DI for middleware/transports.)*
  - ~~Update documentation to reflect simplified registration workflow.~~ *(Docs updated inline here and `docs/plan.md` section refined to describe registry + JSON configuration pipeline.)*

## 3. Dispatcher & Routing (Phase 2)

- **Procedure Catalogue Enhancements**
  - ~~Support hierarchical naming: `service::module::method`, alias resolution.~~ *(completed via alias-aware `ProcedureSpec` + `ProcedureRegistry` updates allowing alternate names and exposing them through introspection.)*
  - ~~Implement router table with wildcard/version matching (e.g., `foo::*`, `foo::v2::*`).~~ *(`ProcedureRegistry` now stores wildcard aliases with specificity scoring so lookups resolve to the most specific match; see `DispatcherTests.RegisterUnary_WildcardAliasRoutesRequests` / `RegisterUnary_WildcardSpecificityPrefersMostSpecificAlias`.)*
  - ~~Add validation to prevent conflicting registrations.~~ *(Wildcard alias registration rejects conflicting patterns, covered by `DispatcherTests.RegisterUnary_DuplicateWildcardAliasThrows`.)*

- **Middleware Composition**
  - ~~Maintain separate inbound/outbound stacks for unary, oneway, client stream, server stream, bidi stream.~~ *(`DispatcherOptions` exposes dedicated lists per RPC type; `DispatcherTests` asserts client configs preserve the typed splits.)*
  - ~~Provide builder APIs to attach middleware during registration (`dispatcher.Register(..., middleware => ...)`).~~ *(`Dispatcher.Register*` overloads now accept fluent builders (`UnaryProcedureBuilder`, `StreamProcedureBuilder`, etc.) with coverage in `DispatcherTests.RegisterUnary_BuilderConfiguresPipelineAndMetadata`.)*
  - ~~Ensure middleware ordering is deterministic and documented.~~ *(Builders append middleware in declaration order after global stacks; ordering is verified in tests and captured in `docs/reference/middleware.md`.)*

- **Introspection Endpoint**
  - ~~Implement HTTP endpoint (`/polymer/introspect`) returning dispatcher summary (service, status, procedures, middleware).~~ *(completed via `HttpInbound.HandleIntrospectAsync` and `HttpIntrospectionTests.IntrospectionEndpoint_ReportsDispatcherState`)*
  - ~~Include unit/integration tests verifying JSON shape.~~ *(covered by `HttpIntrospectionTests.IntrospectionEndpoint_ReportsDispatcherState`)*
  - ~~Group procedures by RPC type and embed streaming-specific metadata (buffer sizing, message counts).~~ *(completed via `ProcedureGroups` descriptors and streaming metadata defaults stored on `StreamProcedureSpec`, `ClientStreamProcedureSpec`, and `DuplexProcedureSpec`.)*
  - ~~Surface outbound/peer chooser state and runtime counters to align with YARPC-Go’s debug output.~~ *(completed via `OutboundDescriptor`/`OutboundBindingDescriptor` and transport diagnostic snapshots such as `GrpcOutboundSnapshot` and `HttpOutboundSnapshot`; still plan richer peer health metrics as choosers evolve.)*

- **Shadowing & Tee Support**
  - ~~Support registering dual outbounds (primary + shadow) for migration scenarios.~~ *(completed via `TeeUnaryOutbound`/`TeeOnewayOutbound` and `DispatcherOptions.AddTee*` helpers.)*
  - ~~Add policy configuration to enable teeing per procedure with percentage sampling.~~ *(handled by `TeeOptions` with sampling, header tagging, and success-only gating.)*
  - ~~Provide tests verifying secondary traffic dispatch and optional response suppression.~~ *(covered by `TeeOutboundTests` exercising success/failure flows and header propagation.)*

## 4. Middleware & Observability (Phase 5)

- **Core Middleware Set**
  - ~~Logging middleware:~~ *(completed via `RpcLoggingMiddleware` + `RpcLoggingOptions` providing structured inbound/outbound logging with sampling controls.)*
  - ~~Tracing middleware:~~ *(completed via `RpcTracingMiddleware` providing ActivitySource-based spans with context extraction/injection and YARPC tag conventions.)*
  - ~~Metrics middleware:~~ *(completed via `RpcMetricsMiddleware` + `RpcMetricsOptions` recording request counters and latency histograms across inbound/outbound/stream pipelines; payload sizing remains future work.)*
  - ~~Deadline enforcement:~~ *(completed via `DeadlineMiddleware`/`DeadlineOptions` enforcing TTL/absolute deadlines with canonical `DeadlineExceeded` errors.)*
  - ~~Panic/exception recovery:~~ *(completed via `PanicRecoveryMiddleware` converting unhandled exceptions into `Internal` errors with logging/metadata.)*
  - ~~Retry/backoff middleware:~~ *(completed via `RetryMiddleware`/`RetryOptions` leveraging Hugo retry policies with per-request selectors.)*
  - ~~Rate limiting / circuit breaking:~~ *(completed via `RateLimitingMiddleware` + `RateLimitingOptions` providing concurrency-based controls with `ResourceExhausted` signaling; circuit breaking will layer on peer subsystem.)*

- **Middleware SDK**
  - ~~Provide context objects exposing metadata, transport info, channel writers (for streaming).~~ *(completed via `StreamCallContext` / `DuplexStreamCallContext` now returned on `IStreamCall`/`IDuplexStreamCall`; follow-up documentation/examples still required.)*
  - ~~Document best practices, sample custom middleware.~~ *(See `docs/reference/middleware.md` for guidance covering unary/outbound examples, streaming tips, and ordering rules.)*
  - Include analyzers or templates for middleware authors.

## 5. Peer Management & Load Balancing (Phase 6)

- **Peer Models**
  - ~~Define `IPeer` with address, connection status, metrics.~~ *(completed via `OmniRelay.Core.Peers` primitives.)*
  - ~~Track inflight requests, last success/failure times.~~ *(maintained by `GrpcPeer` status reporting.)*

- **Peer Lists & Choosers**
  - ~~Implement lists: single, round robin, fewest-pending, two-random choices.~~ *(Round robin, fewest-pending, and two-random choice choosers available via `OmniRelay.Core.Peers`.)*
  - ~~Integrate with dispatcher outbounds to acquire/release peers around each call.~~ *(gRPC outbound now acquires/leases `GrpcPeer` instances.)*
  - ~~Propagate peer state into metrics and retry logic.~~ *(completed via `PeerMetrics` instrumentation + updated `PeerLease`/choosers, and retry middleware emitting peer-aware counters.)*

- **Health, Backoff & Circuit Breaking**
  - ~~Add exponential backoff on repeated failures, half-open testing.~~ *(Completed with `PeerCircuitBreaker` supporting exponential backoff, half-open probe limits, and success thresholds integrated into gRPC peers.)*
  - ~~Surface retryable vs non-retryable errors to chooser.~~ *(gRPC outbound now classifies errors via `OmniRelayErrors.GetFaultType`, avoiding peer penalties for caller faults.)*
  - ~~Provide configuration knobs for thresholds.~~ *(exposed via `PeerCircuitBreakerOptions` consumed by `GrpcOutbound`.)*

- **Peer Introspection**
  - ~~Introspection endpoint to show peer health, latency percentiles.~~ *(completed: `GrpcOutbound` exposes success/failure counters and latency percentiles through `GrpcPeerSummary`, surfaced via `/polymer/introspect`; covered by new telemetry tests ensuring metrics/logging interceptors feed the snapshots.)*
  - ~~Add metrics per peer (success/failure counts, inflight gauge).~~ *(covered by `PeerMetrics` counters/UpDownCounters tagged with peer identifiers during lease acquisition and release.)*

## 6. Error Model Parity (Phase 9)

- **Status Mapping**
  - ~~Ensure HTTP/gRPC/TChannel (if implemented) map all YARPC codes correctly.~~ *(HTTP + gRPC mappings now verified via `HttpStatusMapperTests` and existing `GrpcStatusMapper` coverage; TChannel remains intentionally out of scope for this parity push.)*
  - ~~Define retry hints, fault classification metadata.~~ *(Errors are annotated with `polymer.faultType`/`polymer.retryable` metadata via `OmniRelayErrorAdapter` and surfaced through `OmniRelayErrors.IsRetryable`.)*

- **Error Helpers**
  - ~~Add helper APIs: `OmniRelayErrors.IsStatus`, `GetFaultType`, `GetRetryable`.~~ *(Expanded helpers include `OmniRelayErrors.IsRetryable(...)` and metadata-aware fault classification.)*
  - ~~Provide ASP.NET + gRPC exception adapters (filters/interceptors).~~ *(Implemented `OmniRelayExceptionFilter` for ASP.NET Core and `GrpcExceptionAdapterInterceptor` for gRPC servers, ensuring thrown exceptions surface OmniRelay metadata uniformly.)*
  - ~~Document canonical error handling patterns.~~ *(Captured in `docs/reference/errors.md`, covering ASP.NET/gRPC adapters, client guidance, and metadata expectations.)*

## 7. Configuration System (Phase 7)

- ~~**Declarative Bootstrap**~~ *(Delivered via the new `src/OmniRelay.Configuration` project. `OmniRelayConfigurationOptions` captures transports/peers/middleware, `AddOmniRelayDispatcher` wires everything into DI, and validation is covered by `OmniRelayConfigurationTests`.)*

- ~~**Transport/Peer Specs**~~ *(`ICustomInboundSpec`, `ICustomOutboundSpec`, and `ICustomPeerChooserSpec` let DI registered components materialize transports/peer choosers from configuration. Covered by `OmniRelayConfigurationTests.AddOmniRelayDispatcher_UsesCustomTransportSpecs` and `.UsesCustomPeerSpec`.)*

- **Environment Overrides**
  - ~~Support layered configuration (appsettings + env vars + command line).~~ *(Binder operates on `IConfiguration`, so configuration layering works out of the box; follow-up task is documenting best practices.)*
  - ~~Provide sample `appsettings.json` showing multi-environment overrides.~~ *(See `docs/reference/configuration` for base/development/production examples and README links.)*

## 8. Tooling & Introspection

- **CLI Support**
  - ~~Develop `omnirelay` CLI (or adapt `yab`) for issuing test requests, inspecting dispatcher state, validating configuration. *(Initial `yab` harness available under `tests/OmniRelay.YabInterop`; expand to richer CLI and automation.)*~~ *(shipped via `src/OmniRelay.Cli` with docs/reference/cli.md)*
  - ~~Add codec-aware presets (json/protobuf helpers) and package the CLI as a dotnet global tool once the surface area settles.~~ *(Profiles landed in `src/OmniRelay.Cli/Program.cs`, protobuf descriptors supported via `--proto-file`, new automation in `docs/reference/cli-scripts`, and the project now packs as `OmniRelay.Cli`.)*
  - ~~Include scripting/automation examples.~~ *(Documented in `docs/reference/cli.md` with `omnirelay-smoke.sh` helper.)*

- **Diagnostics**
  - ~~Expose metrics via OpenTelemetry exporters (Prometheus OTLP).~~
  - ~~Add logging enrichers (request id, peer info).~~
  - ~~Implement runtime toggles (e.g., log level, sampling) via control plane endpoint or config reload.~~

- **Examples & Samples**
  - ~~Provide sample services demonstrating HTTP unary + oneway, gRPC unary + streaming, and middleware + peer chooser configuration.~~ *(Implemented via `samples/Quickstart.Server/Program.cs` with JSON codecs across transports.)*
  - ~~Add detailed documentation and quickstart tutorials.~~ *(See `docs/reference/quickstart.md` for end-to-end commands.)*

## 9. Interop & Testing (Phase 11)

- **Conformance Suite**
  - Build test harness running OmniRelay server against `yarpc-go` clients (HTTP + gRPC).
  - Verify metadata propagation, streaming semantics, error codes, deadlines.
  - Mirror YARPC crossdock tests if feasible.

- **Performance Benchmarks**
  - ~~Provide scripts/infrastructure (e.g., `yab`, `wrk`, `ghz`) to measure throughput/latency.~~ *(Initial `yab` script + echo harness in `tests/OmniRelay.YabInterop`; extend with benchmarking tools.)*
  - Capture baseline metrics for releases and regressions.

- **CI Enhancements**
  - Configure matrix builds (Windows, Linux, macOS) and multiple .NET versions.
  - Add static analysis (nullable warnings, analyzers), formatting, and package validation steps.

## 10. TChannel & Legacy Support (Optional)

- Evaluate demand for TChannel parity.
- If needed, design inbound/outbound using existing Go reference:
  - Frame encoding/decoding, connection pooling, peer integration.
  - Ensure compatibility with legacy YARPC clients.

---

Revisit this backlog after each milestone to break the remaining items into issues/priorities and to ensure alignment with the original project plan.
