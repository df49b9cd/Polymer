# Polymer Parity TODO

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
      - Provide context objects for stream state (message count, completion reason).
    - Tests:
      - ~~Add gRPC duplex integration coverage mirroring the HTTP echo scenario (currently only `HttpTransportTests.DuplexStreaming_OverHttpWebSocket` exercises this path).~~ *(completed via `GrpcTransportTests.DuplexStreaming_OverGrpcTransport`)
      - ~~Automate gRPC flow-control scenario (server slower than client).~~ *(completed via `GrpcTransportTests.DuplexStreaming_FlowControl_ServerSlow`)
      - ~~Verify cancellation initiated by server and by client over gRPC.~~ *(completed via `GrpcTransportTests.DuplexStreaming_ServerCancellationPropagatesToClient` and `GrpcTransportTests.DuplexStreaming_ClientCancellationPropagatesToServer`)
      - ~~Validate metadata propagation (custom headers/trailers) for gRPC duplex streams.~~ *(completed via `GrpcTransportTests.DuplexStreaming_PropagatesMetadata`)
  - **Shared streaming tasks**
    - ~~Update dispatcher introspection to list available stream procedures and status (unary, client, server, bidi).~~ *(completed)*
    - Document public APIs (how to build client streaming/bidi handlers).
    - ~~Expand gRPC outbound/inbound error handling to surface canonical codes for streaming faults.~~ *(completed)*
    - **gRPC transport test gaps (parity audit)**
      - Cover duplex/bidirectional streaming happy path, cancellation, and flow-control scenarios.
      - Validate request/response metadata propagation (`rpc-*` headers, custom headers, TTL/deadline) across unary and streaming RPCs.
      - Assert response trailers carry `polymer-encoding`, status, and error metadata for success & failure cases.
      - Verify `GrpcStatusMapper` mappings for all canonical codes surfaced by unary and streaming transports.
      - Exercise streaming edge conditions: server-initiated cancellation/error for server and client streams, partial writes, and mid-stream failures.
      - Add regression coverage once upstream enables forcing request/response compression so we can validate advertised `grpc-accept-encoding` behavior.
      - Compression negotiation coverage: advertise supported compressors (`grpc-accept-encoding`) and verify request/response compression once upstream exposes the necessary hooks.

    - Peer management & load balancing: GrpcOutbound binds to a single _address and opens one GrpcChannel with no peer chooser, retention, or backoff logic (src/Polymer/Transport/Grpc/GrpcOutbound.cs (lines 17-45)). YARPC-Go’s gRPC transport fronts multiple peers with choosers, dial-time backoff, and per-peer lifecycle management, so we currently lack parity on resiliency and multi-host routing.

    - Observability & middleware: Added built-in logging/metrics interceptors (`GrpcClientLoggingInterceptor`, `GrpcServerLoggingInterceptor`) and runtime hooks; still need richer metrics coverage across streaming RPCs and integration with central telemetry config.

    - ~~Security & connection tuning: The inbound config only forces HTTP/2 listeners and never exposes TLS, keepalive, or max message size knobs (src/Polymer/Transport/Grpc/GrpcInbound.cs (lines 60-71)); the outbound constructor similarly only tweaks the HTTP handler (src/Polymer/Transport/Grpc/GrpcOutbound.cs (lines 27-39)). YARPC-Go ships options for server/client TLS credentials, keepalive params, compressors, and header size limits, so our transport is missing that flexibility.~~ *(completed via `GrpcServerTlsOptions`, `GrpcClientTlsOptions`, runtime keepalive/message-size options, and `GrpcCompressionOptions`; request/response compression pipeline remains to be validated.)*
    
    - Operational introspection: Our gRPC types implement only the Polymer transport interfaces (src/Polymer/Transport/Grpc/GrpcOutbound.cs (lines 15-25); src/Polymer/Transport/Grpc/GrpcInbound.cs (lines 17-42)) and expose no running-state, peer list, or metrics surfaces. YARPC-Go’s inbound/outbound implement introspection APIs and structured logging, so we lack comparable admin visibility.
    
    - Deadline/TTL enforcement & header hygiene: We serialize TTL/deadline information into metadata (src/Polymer/Transport/Grpc/GrpcMetadataAdapter.cs (lines 75-88)) but never convert it into gRPC deadlines in CallOptions, so the runtime won’t actually enforce them (src/Polymer/Transport/Grpc/GrpcOutbound.cs (lines 61-174)). YARPC-Go validates headers, propagates TTLs into contexts, and distinguishes application errors via metadata, which leaves us short on request semantics.


- **Transport Middleware & Interceptors**
  - Design middleware interfaces for transport-specific hooks:
    - HTTP message handler chain for outbound requests (pre/post).
    - gRPC interceptors for unary and streaming calls (client + server).
  - Integrate transport middleware with dispatcher pipeline ordering (global → per-procedure).
  - Provide sample middleware (e.g., logging interceptor) to demonstrate usage.

- **Transport Lifecycle & Health**
  - Implement graceful shutdown semantics:
    - HTTP: return 503 with `Retry-After`, drain active requests.
    - gRPC: send GOAWAY, wait for inflight RPCs to finish.
  - Health/readiness endpoints:
    - HTTP `/healthz` & `/readyz` factoring transport + peer state.
    - gRPC health check service implementation.
  - Tests covering fast stop (force cancel) vs graceful stop.

## 2. Encodings & Code Generation (Phase 8)

- **Raw/Binary Encoding**
  - Create `RawCodec` (byte[] passthrough) with metadata enforcement.
  - Ensure HTTP/gRPC transports propagate binary content-type headers correctly.
  - Add tests ensuring raw payloads bypass serialization.

- **Protobuf Support**
  - Author `protoc-gen-polymer-csharp` plugin:
    - Generate request/response DTOs, codecs, dispatcher registration stubs, client helpers.
    - Provide MSBuild integration / tooling instructions.
  - Support Protobuf over gRPC (native) and optional HTTP JSON + Protobuf:
    - Handle content negotiation & media types.
  - Write codegen tests (golden outputs) and integration tests round-tripping messages across transports.

- **Thrift Encoding**
  - Investigate options: port ThriftRW vs using Apache Thrift.
  - Implement codec translating between Thrift `TProtocol` payloads and `Result<T>`.
  - Generate server/client wrappers from IDL (with examples).
  - Add interop tests using YARPC fixtures.

- **JSON Enhancements**
  - Allow custom `JsonSerializerOptions` via configuration (per-procedure overrides).
  - Add source-generated context support for performance.
  - Optional: schema validation hooks leveraging JSON Schema.

- **Codec Registry**
  - Introduce registry at dispatcher level mapping procedure → codec.
  - Provide DI integration so transports autoselect codecs without manual wiring.
  - Update documentation to reflect simplified registration workflow.

## 3. Dispatcher & Routing (Phase 2)

- **Procedure Catalogue Enhancements**
  - Support hierarchical naming: `service::module::method`, alias resolution.
  - Implement router table with wildcard/version matching (e.g., `foo::*`, `foo::v2::*`).
  - Add validation to prevent conflicting registrations.

- **Middleware Composition**
  - Maintain separate inbound/outbound stacks for unary, oneway, client stream, server stream, bidi stream.
  - Provide builder APIs to attach middleware during registration (`dispatcher.Register(..., middleware => ...)`).
  - Ensure middleware ordering is deterministic and documented.

- **Introspection Endpoint**
  - Implement HTTP endpoint (`/polymer/introspect`) returning:
    - Registered procedures grouped by RPC type.
    - Transport inbounds/outbounds, middleware chains, peer choosers.
    - Streaming procedure metadata (buffer sizes, message counts).
  - Ensure data updates dynamically as dispatcher state changes.
  - Include unit/integration tests verifying JSON shape.

- **Shadowing & Tee Support**
  - Support registering dual outbounds (primary + shadow) for migration scenarios.
  - Add policy configuration to enable teeing per procedure with percentage sampling.
  - Provide tests verifying secondary traffic dispatch and optional response suppression.

## 4. Middleware & Observability (Phase 5)

- **Core Middleware Set**
  - Logging middleware:
    - Structured logging for inbound/outbound (request id, procedure, duration) with sampling controls.
  - Tracing middleware:
    - OpenTelemetry integration with span creation, context propagation (HTTP headers + gRPC metadata).
  - Metrics middleware:
    - Expose counters/histograms for requests, latency, retries, payload sizes.
  - Deadline enforcement:
    - Ensure requests respect TTL/deadline metadata, convert to canonical errors.
  - Panic/exception recovery:
    - Catch unhandled exceptions, translate to `Internal` with stack metadata.
  - Retry/backoff middleware:
    - Utilize Hugo result policies; configure per-procedure.
  - Rate limiting / circuit breaking:
    - Token bucket, sliding window, or concurrency-based controls; propagate backoff metadata.

- **Middleware SDK**
  - Provide context objects exposing metadata, transport info, channel writers (for streaming).
  - Document best practices, sample custom middleware.
  - Include analyzers or templates for middleware authors.

## 5. Peer Management & Load Balancing (Phase 6)

- **Peer Models**
  - Define `IPeer` with address, connection status, metrics.
  - Track inflight requests, last success/failure times.

- **Peer Lists & Choosers**
  - Implement lists: single, round robin, fewest-pending, two-random choices.
  - Integrate with dispatcher outbounds to acquire/release peers around each call.
  - Propagate peer state into metrics and retry logic.

- **Health, Backoff & Circuit Breaking**
  - Add exponential backoff on repeated failures, half-open testing.
  - Surface retryable vs non-retryable errors to chooser.
  - Provide configuration knobs for thresholds.

- **Peer Introspection**
  - Introspection endpoint to show peer health, latency percentiles.
  - Add metrics per peer (success/failure counts, inflight gauge).

## 6. Error Model Parity (Phase 9)

- **Status Mapping**
  - Ensure HTTP/gRPC/TChannel (if implemented) map all YARPC codes correctly.
  - Define retry hints, fault classification metadata.

- **Error Helpers**
  - Add helper APIs: `PolymerErrors.IsStatus`, `GetFaultType`, `GetRetryable`.
  - Provide ASP.NET + gRPC exception adapters (filters/interceptors).
  - Document canonical error handling patterns.

## 7. Configuration System (Phase 7)

- **Declarative Bootstrap**
  - Build `Polymer.Configuration` package:
    - YAML/JSON schema for services, transports, peers, middleware.
    - DI integration (`AddPolymerDispatcher(configuration)`).
    - Validation with detailed error messages.

- **Transport/Peer Specs**
  - Allow registration of custom `TransportSpec`/`PeerListSpec` via DI so new transports plug into configuration.

- **Environment Overrides**
  - Support layered configuration (appsettings + env vars + command line).
  - Provide sample `appsettings.json` showing multi-environment overrides.

## 8. Tooling & Introspection

- **CLI Support**
  - Develop `polymer` CLI (or adapt `yab`) for issuing test requests, inspecting dispatcher state, validating configuration.
  - Include scripting/automation examples.

- **Diagnostics**
  - Expose metrics via OpenTelemetry exporters (Prometheus OTLP).
  - Add logging enrichers (request id, peer info).
  - Implement runtime toggles (e.g., log level, sampling) via control plane endpoint or config reload.

- **Examples & Samples**
  - Provide sample services demonstrating:
    - HTTP unary + oneway.
    - gRPC unary + streaming.
    - Middleware usage, peer chooser configuration.
  - Add detailed documentation and quickstart tutorials.

## 9. Interop & Testing (Phase 11)

- **Conformance Suite**
  - Build test harness running Polymer server against `yarpc-go` clients (HTTP + gRPC).
  - Verify metadata propagation, streaming semantics, error codes, deadlines.
  - Mirror YARPC crossdock tests if feasible.

- **Performance Benchmarks**
  - Provide scripts/infrastructure (e.g., `yab`, `wrk`, `ghz`) to measure throughput/latency.
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
