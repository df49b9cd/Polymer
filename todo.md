# HTTP/3 Parity TODO

## Infrastructure & Platform

- [ ] Guarantee every runtime environment meets QUIC prerequisites (Windows 11/Server 2022 or Linux with `libmsquic` ≥ 2.2, TLS 1.3 capable certs).
- [ ] Ensure certificate provisioning pipelines allow TLS 1.3 cipher suites (`TLS_AES_*`, `TLS_CHACHA20_*`) and rotate any legacy certs that fail QUIC handshakes.
- [ ] Automate `libmsquic` installation for supported Linux distros in provisioning scripts and document verification steps.
- [ ] Confirm Kubernetes ingress / load balancers / CDNs can forward UDP/QUIC traffic on required ports; document any appliances that terminate HTTP/3.
- [ ] Verify edge devices emit `alt-svc` headers unchanged and that UDP 443 is opened wherever TCP 443 is already allowed.
- [ ] Open firewall rules for UDP alongside existing TCP listeners; add health checks for dropped QUIC packets.
- [ ] Update deployment runbooks to include QUIC troubleshooting commands (`ss -u`, `netsh ... excludedportrange`, etc.).

## Kestrel Listener Configuration

- [ ] Add an HTTP/3 feature flag in transport configuration (opt-in per listener and environment).
- [ ] Update `HttpInbound` listeners to use `HttpProtocols.Http1AndHttp2AndHttp3` and validate TLS 1.3 availability before enabling HTTP/3.
- [ ] Update `GrpcInbound` listeners to allow HTTP/3 (ensure MsQuic handshake succeeds when TLS callbacks are configured).
- [ ] Confirm endpoint configuration sets `ListenOptions.Protocols` and `EnableAltSvc` equivalents so HTTP/3 advertises correctly across all bindings.
- [ ] Surface MsQuic-specific tuning knobs (connection idle timeout, stream limits, keep-alive) via runtime options with sensible defaults.
- [ ] Ensure graceful shutdown/drain logic works for QUIC transports and still signals retry metadata (`retry-after`) consistently.

## Feature Parity: HTTP Surface

- [ ] Verify `/omnirelay/introspect`, `/healthz`, `/readyz`, and other existing HTTP endpoints work over HTTP/3 and remain backward compatible with HTTP/1.1/2.
- [ ] Revisit WebSocket usage in `HttpInbound`: document that classic WebSockets stay on HTTP/1.1 and confirm duplex scenarios have an HTTP/3-friendly alternative (e.g., HTTP/3 streams or keep HTTP/1.1 fallback).
- [ ] Confirm streaming/duplex pipelines (pipe-based framing, large payload support) handle QUIC flow control limits; tune frame size when necessary.
- [ ] Ensure request/response size limits, header validation, and timeouts map to QUIC semantics; add tests for limits enforcement under packet loss.
- [ ] Validate error handling and status mapping (including retry headers) behaves identically when requests fall back from HTTP/3 to HTTP/2/1.1.

## Feature Parity: gRPC Surface

- [ ] Validate gRPC interceptors, compression providers, and telemetry hooks operate identically when the transport upgrades to HTTP/3.
- [ ] Update `GrpcOutbound`/gRPC-dotnet client construction to opt into HTTP/3 per https://learn.microsoft.com/en-us/aspnet/core/grpc/troubleshoot?view=aspnetcore-9.0#configure-grpc-client-to-use-http3 and expose configuration for disabling when peers lack QUIC.
- [ ] Confirm client handlers set `HttpRequestMessage.VersionPolicy = RequestVersionOrHigher` (or stricter policies) and tune `SocketsHttpHandler` HTTP/3 settings (keep-alive, connection pooling) so call concurrency matches HTTP/2 parity targets.
- [ ] Exercise server keep-alive settings under HTTP/3 and expose MsQuic keep-alive knobs alongside current HTTP/2 configuration.
- [ ] Confirm gRPC health checks and draining semantics still produce `StatusCode.Unavailable` with retry metadata.
- [ ] Capture and document any gRPC client library limitations (per language) when connecting over HTTP/3.
- [ ] Update protobuf code generation templates to emit HTTP/3-aware client/channel wiring (e.g., default `GrpcChannelOptions` with HTTP/3 enabled).

## Outbound Calls & YARP Integrations

- [ ] Allow `HttpOutbound` and other client stacks to request HTTP/3 (`HttpVersion.Version30` or `HttpVersionPolicy.RequestVersionOrHigher`) with configurable fallback.
- [ ] Extend service discovery / routing metadata to prefer HTTP/3 endpoints when available and handle negotiation failures cleanly.
- [ ] Verify `GrpcOutbound` channel pooling, circuit breaker, and retry logic behave with HTTP/3-only endpoints and surface telemetry when peers fall back to HTTP/2.
- [ ] Verify upstream proxies and service meshes accept HTTP/3 from OmniRelay; document any services limited to HTTP/2/1.1.
- [ ] Update CLI commands (`serve`, `bench`, etc.) to expose HTTP/3 flags and ensure generated configuration includes HTTP/3 endpoints when enabled.

## Compatibility & Rollout

- [ ] Build protocol negotiation health checks that alert when HTTP/3 fails and traffic reverts to HTTP/2/1.1 beyond an acceptable threshold.
- [ ] Provide runtime configuration switches to disable HTTP/3 per listener/service without redeploying binaries.
- [ ] Document rollout sequencing (staging → canary → production) including verification of `alt-svc` propagation and UDP reachability at each phase.

## Observability & Telemetry

- [ ] Add structured logging for QUIC connection lifecycle (handshake failures, congestion, migration) and integrate with existing metrics.
- [ ] Emit protocol-level metrics (per-protocol request counts, handshake RTT) so we can compare HTTP/3 vs HTTP/2/1.1 performance.
- [ ] Wire up distributed tracing spans to capture HTTP/3 attributes (QUIC connection id, protocol version) for correlation.
- [ ] Track fallback rates (HTTP/3 → HTTP/2/1.1) and QUIC-specific failure codes in dashboards to monitor parity drift.

## Testing & Validation

- [ ] Add integration tests that exercise HTTP/3 requests using `HttpClient` with `RequestVersionOrHigher` and validate fallback to HTTP/2 when QUIC is disabled.
- [ ] Add gRPC client/server integration tests that force HTTP/3 (using gRPC-dotnet HTTP/3 configuration) and assert interceptor behavior, deadlines, streaming semantics, and metadata parity.
- [ ] Update protobuf-generated service tests to cover HTTP/3 call paths and ensure generated clients honor HTTP/3 configuration defaults.
- [ ] Run soak/performance tests focusing on high connection churn, packet loss, and mobile-like IP migration scenarios.
- [ ] Include automated canary validation (curl `--http3`, browser devtools) in deployment pipelines to confirm `alt-svc` advertisement and successful upgrades.
- [ ] Test downgrade scenarios (server disables HTTP/3 mid-flight, UDP blocked, mismatched ALPN) to prove resiliency and useful error messages.
- [ ] Document a rollback strategy that forces listeners back to HTTP/1.1/2 if HTTP/3 issues arise.

## Documentation & Support

- [ ] Update developer docs to explain how to enable HTTP/3 locally (Windows vs. macOS limitations) and in staging/production.
- [ ] Provide FAQ covering common QUIC troubleshooting findings (ALPN mismatch, port reuse behavior, IPv6 nuances).
- [ ] Outline compatibility matrix of supported client SDKs/protocol features over HTTP/3 to set expectations with consumers.
- [ ] Add gRPC-specific HTTP/3 guidance (client configuration snippets, known limitations, fallback instructions) for service owners.
- [ ] Refresh CLI help/docs to include HTTP/3 deployment steps, toggle flags, and troubleshooting guidance.
- [ ] Document protobuf codegen updates so generated client/server stubs surface HTTP/3 configuration options.
