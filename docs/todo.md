# Polymer Parity TODO

Comprehensive backlog to track remaining work required to reach feature parity with `yarpc-go`. Items are grouped by subsystem and reference corresponding plan phases where possible.

## 1. Transports

- ~~**HTTP Oneway Completion (Phase 3)**~~ *(completed)*

- ~~**HTTP Streaming & Advanced Features**~~ *(completed: SSE / chunked server streaming support)*

- **gRPC Oneway & Streaming (Phase 4)**
  - Implement unary->oneway adaptation (e.g., map to empty responses) or adopt proper gRPC oneway semantics (which YARPC models via unary with ack).
  - Server streaming: expose dispatcher `InvokeServerStreamAsync`, bridging to `IAsyncStreamReader`/`IServerStreamWriter` via codecs and middleware.
  - Client & bidi streaming support with backpressure propagation and metadata handling.
  - Expand inbound provider to register streaming methods; update outbound to leverage `AsyncServerStreamingCall`, `AsyncClientStreamingCall`, and `DuplexStreamingCall`.
  - Add end-to-end tests for each stream type (echo, large payload, cancellation, deadline).

- **Transport Middleware & Interceptors**
  - Introduce per-transport middleware hooks (HTTP message handlers, gRPC interceptors) for tracing/metrics logging.
  - Ensure middleware ordering respects global + per-procedure stacks across RPC types.

- **Transport Lifecycle & Health**
  - Implement graceful shutdown hooks signalling draining to peers (HTTP 503 with retry-after, gRPC GOAWAY).
  - Add readiness/health endpoints aligning with YARPC’s introspection.

## 2. Encodings & Code Generation (Phase 8)

- **Raw/Binary Encoding**
  - Provide pass-through codecs for arbitrary byte payloads with metadata mapping.

- **Protobuf Support**
  - Implement `protoc-gen-polymer-csharp` plugin generating dispatcher registrations, client facades, request/response models.
  - Support both gRPC (native) and HTTP/JSON-over-Protobuf transport combos when enabled via configuration.
  - Add tests verifying generated code compiles and round-trips through transports.

- **Thrift Encoding**
  - Evaluate porting ThriftRW or leveraging existing .NET Thrift libraries.
  - Generate adapters for Thrift IDL: server stubs, clients, encoding metadata.
  - Provide integration tests using sample Thrift IDL aligning with yarpc-go fixtures.

- **JSON Enhancements**
  - Support configurable `JsonSerializerOptions`, optional source-generated contexts for perf.
  - Add schema validation hooks (optional).

- **Codec Registry**
  - Dispatcher-level codec registry keyed by procedure to simplify inbound/outbound codec selection instead of manual wiring.

## 3. Dispatcher & Routing (Phase 2)

- **Procedure Catalogue Enhancements**
  - Support multiple procedures per service with namespace separation (e.g., `service::method`), including alias/aliasing logic.
  - Add router table supporting wildcard and versioned procedure names similar to YARPC’s procedure spec.

- **Middleware Composition**
  - Expand dispatcher to maintain separate middleware stacks per RPC type (unary/oneway/stream) for both inbound and outbound, including ordering guarantees, chaining, and per-procedure overrides.
  - Provide fluent registration API to attach middleware when registering procedures.

- **Introspection Endpoint**
  - Implement HTTP introspection endpoint (`/polymer/introspect`) returning JSON snapshot of procedures, transports, middleware, peers (Phase 10).
  - Ensure data stays in sync with dispatcher state and includes health information.

- **Shadowing & Tee Support**
  - Allow registering dual outbounds for traffic mirroring (YARPC “yarpc/transport/http/mediator” equivalent).

## 4. Middleware & Observability (Phase 5)

- **Core Middleware Set**
  - Logging (structured request/response logging with sampling).
  - Tracing (OpenTelemetry integration, automatic span creation, propagation between HTTP headers & gRPC metadata).
  - Metrics (request counts, latencies, error/fault counters, inflight gauge).
  - Deadline enforcement & timeout mapping.
  - Panic/exception recovery translating to canonical statuses.
  - Retry & backoff middleware leveraging Hugo result pipelines.
  - Rate limiting / circuit breaker middleware for inbound/outbound.

- **Middleware SDK**
  - Provide abstractions to simplify authoring third-party middleware, including context objects, short-circuit support, and metadata access.

## 5. Peer Management & Load Balancing (Phase 6)

- **Peer Models**
  - Define `IPeer`, `PeerStatus`, inflight tracking, connection state.
  - Implement peer lists: single, round-robin, fewest-pending, two-random-choices.

- **Choosers & Outbound Integration**
  - Wrap outbounds with chooser logic (`AcquirePeer`, `ReleasePeer` hooks) ensuring errors feed health signals.
  - Support peer updates (add/remove) at runtime with thread-safe state transitions.

- **Health & Backoff**
  - Implement circuit breaker/backoff policies, success thresholds, retryable error classification.

- **Peer Introspection**
  - Expose peer stats (latency, success rates) via introspection and metrics.

## 6. Error Model Parity (Phase 9)

- **Status Mapping**
  - Expand HTTP and gRPC mappings for all YARPC codes (client/server fault classification, retry hints).
  - Ensure transport-specific metadata (headers, trailers) include canonical code names, message, cause stack.

- **Error Helpers**
  - Add `IsStatus(error, code)`, `GetFaultType` parity, retryability metadata.
  - Provide transformation helpers for bridging exceptions ↔ Polymer exceptions (ASP.NET filters, gRPC interceptors).

## 7. Configuration System (Phase 7)

- **Declarative Bootstrap**
  - Implement `Polymer.Configuration` package supporting YAML/JSON config (service name, inbounds, outbounds, transports, peers, middleware).
  - Provide `TransportSpec` / `PeerListSpec` registration akin to YARPC’s plugin architecture.
  - Integrate with `HostBuilder` via `AddPolymerDispatcher(configSection)` extension.
  - Validation + diagnostics (missing service name, duplicate keys, unsupported combinations).

- **Environment Overrides**
  - Support binding from appsettings/environment variables, layered configuration (dev/prod).

## 8. Tooling & Introspection

- **CLI Support**
  - Provide `polymer` CLI for introspection, configuration validation, sending test requests (or adapt `yab` integration).

- **Diagnostics**
  - Expose metrics exporters (Prometheus, OpenTelemetry).
  - Add logging enrichers with request context metadata.
  - Support runtime toggles (log level, sampling).

- **Examples & Samples**
  - Build sample services (keyvalue, echo) demonstrating HTTP & gRPC, oneway, streaming, middleware usage.
  - Provide documentation & quickstart guides mirroring YARPC docs.

## 9. Interop & Testing (Phase 11)

- **Conformance Suite**
  - Create integration harness running Polymer server against yarpc-go clients (HTTP/gRPC) verifying metadata, deadlines, errors, streaming.
  - Mirror YARPC crossdock tests if feasible.

- **Performance Benchmarks**
  - Provide scripts to run `yab` load tests; collect baseline latency/QPS.
  - Add benchmarks for codecs, middleware, peer choosers.

- **CI Enhancements**
  - Multi-target test matrix (Windows/Linux/macOS) and multiple .NET versions.
  - Static analysis (nullable warnings, analyzers), formatting checks, packaging validation.

## 10. TChannel & Legacy Support (Optional)

- Evaluate feasibility/priority of TChannel transport parity; if needed, plan similar inbound/outbound integration leveraging existing Go behaviour as reference.

---

This list should be revisited after each major milestone to break down tasks into implementable issues/PBI stories and to confirm sequencing with the original phase plan. 
