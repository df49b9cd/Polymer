# OmniRelay Integration Test Coverage Plan

Track high-value integration scenarios that prove the dispatcher, transports, configuration, and tooling behave correctly end-to-end. Each checklist item should become a concrete test fixture (or suite) under `tests/OmniRelay.IntegrationTests`.

## Hosting & Configuration

- [x] Host dispatcher via `AddOmniRelayDispatcher` using multiple configuration sources (JSON + environment overlays) and verify the DI graph wires up expected components. (Covered by `HostingConfigurationIntegrationTests`)
- [x] Cover multiple inbound definitions (HTTP + gRPC + custom specs) to ensure named lifecycles start/stop and expose introspection metadata. (`HostingConfigurationIntegrationTests`)
- [ ] Validate outbound binding for every RPC shape (unary, oneway, server/client/duplex stream) when configured purely through the `omnirelay` section.
- [ ] Assert feature flags toggle runtime options at startup (HTTP/3 enablement, middleware switches, codec policies).

## HTTP Transport Coverage

- [ ] Exercise HTTP/1.1, HTTP/2, and HTTP/3 listeners with real TLS certificates, confirming protocol negotiation, alt-svc headers, and upgrade/fallback behavior.
- [ ] Validate request/response headers (Rpc-*) mirror expectations for success and failure paths, including JSON/protobuf payloads.
- [ ] Test outbound HTTP clients configured for different `HttpVersionPolicy` values, ensuring retries across peers and fallback from HTTP/3 → HTTP/2.
- [ ] Include SSE/streaming scenarios to ensure chunked bodies and throttling limits work over real sockets.

## gRPC Transport Coverage

- [ ] Cover unary and all streaming shapes over HTTP/2 and HTTP/3, asserting metadata, deadlines, and interceptor behaviors.
- [ ] Verify TLS certificate loading, client authentication modes, and ALPN enforcement.
- [ ] Ensure MsQuic-specific tuning (stream limits, idle timeout, keep-alive) takes effect or logs warnings as expected.
- [ ] Run integration tests that use generated protobuf services to confirm server + client stubs honor transport settings.

## Codegen & Client Workflows

- [ ] Round-trip generated clients against a dispatcher host, including HTTP/3-enabled channels and fallback to HTTP/2.
- [ ] Validate client-stream/duplex helpers from codegen interoperate with the dispatcher when instantiated via DI.
- [ ] Ensure codegen-produced registration helpers wire middleware/encodings identically to manual registrations.

## CLI & Tooling

- [ ] Invoke `omnirelay config validate` and `omnirelay serve` within tests to assert configs produced by the CLI bootstrap hosts successfully.
- [ ] Run `omnirelay introspect/request/benchmark` against live hosts and assert output (text + JSON) matches the service wiring.
- [ ] Cover HTTP/3-related CLI flags to ensure they propagate into generated config and result in QUIC listeners.

## Observability & Diagnostics

- [ ] Assert `/omnirelay/introspect`, `/healthz`, and `/readyz` endpoints respond with expected payloads under normal and degraded conditions.
- [ ] Verify structured logging and metrics emit protocol version, peer selection, and middleware traces in end-to-end scenarios.
- [ ] Confirm OpenTelemetry spans capture transport attributes (protocol, connection id) and survive through streaming calls.

## Resiliency & Failure Modes

- [ ] Test graceful shutdown/drain for HTTP and gRPC inbounds with concurrent requests, ensuring Retry-After headers/trailers surface.
- [ ] Simulate transport failures (peer down, TLS handshake failure, HTTP/3 blocked) and confirm fallback strategies and error metadata.
- [ ] Drive deadline and cancellation scenarios end-to-end to ensure callers receive the correct statuses and retry hints.
- [ ] Validate circuit breaker + retry middleware interactions by orchestrating failing peers and recovering traffic.

## Compatibility & Interop

- [ ] Add yab (or grpcurl/curl --http3) harnesses to verify OmniRelay interops with non-.NET clients over HTTP/1.1/2/3.
- [ ] Ensure proxy/ingress scenarios (Envoy, YARP) forward OmniRelay headers and protocol negotiations correctly.
- [ ] Capture rolling upgrade / shadow traffic tests to validate tee/shadow metadata routes requests to both clusters with matching responses.
