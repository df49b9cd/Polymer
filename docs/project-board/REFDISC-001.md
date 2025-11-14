# REFDISC-001 - Shared Control-Plane Host Builders

## Goal
Factor the dispatcher's gRPC HTTP/3 and REST HTTP/2 inbound hosts into reusable builders so every control-plane service can expose secure endpoints with the same TLS, middleware, and diagnostics guarantees without instantiating the dispatcher runtime.

## Scope
- Extract the QUIC/Kestrel configuration, endpoint binding, and TLS enforcement logic currently embedded in `GrpcInbound`.
- Extract the ASP.NET Core/Kestrel configuration plus middleware wiring from `HttpInbound`/`HttpInboundMiddleware`.
- Provide purpose-built builders (`GrpcHttp3HostBuilder`, `HttpControlPlaneHostBuilder`) that accept service registrations, diagnostics modules, and transport options, returning configured `WebApplication` instances.
- Ensure the builders natively integrate with shared interceptor/middleware registries, diagnostics kits, and telemetry modules so hosts remain lightweight.
- Document how control-plane services compose the builders and register gRPC services, minimal APIs, and diagnostics routes without referencing dispatcher assemblies.

## Requirements
1. **Protocol coverage** - gRPC hosts must negotiate HTTP/3 when available and downgrade to HTTP/2; HTTP hosts must support HTTP/2 with HTTP/1.1 fallback and dispatcher-equivalent keep-alive/backpressure defaults.
2. **mTLS parity** - Support `ClientCertificateMode.RequireCertificate`, certificate rotation, revocation toggles, and thumbprint pinning across both builders so TLS behavior matches dispatcher baselines.
3. **Runtime/option parity** - Expose configuration for QUIC idle timeout, bidirectional stream limits, compression, request body limits, keep-alive, and rate limiting so existing `transport:*` settings map 1:1.
4. **Cross-cutting integration** - Accept the shared interceptor/middleware registries, diagnostics runtime, and telemetry module so logging, tracing, auth, and `/omnirelay/control/*` endpoints behave consistently.
5. **Observability** - Emit the current transport metrics/events (QUIC diagnostics, HTTP counters, OpenTelemetry spans) and surface them through the diagnostics registration path with no naming drifts.

## Deliverables
- Two host builder APIs + implementations under `OmniRelay.Transport.Grpc` and `OmniRelay.Transport.Http`.
- Refactoring of `GrpcInbound` and `HttpInbound` to consume the builders (proving backward compatibility).
- Wiring updates for gossip/leadership/bootstrap hosts to consume the builders instead of ad-hoc `WebApplication` construction.
- Documentation describing configuration keys, security guidance, and migration steps for service-discovery components.

## Acceptance Criteria
- Dispatcher gRPC and HTTP inbound paths continue to bind successfully with the builders and honor all TLS/runtime settings.
- Control-plane hosts can expose gRPC and REST endpoints through the builders without referencing dispatcher-specific code.
- HTTP/3 downgrade + HTTP/2 fallback scenarios pass integration tests across QUIC-enabled/disabled environments.
- Interceptors/middleware registered through the builders fire for both dispatcher and control-plane endpoints, and diagnostics endpoints remain available.
- Prometheus/OpenTelemetry transport metrics remain unchanged when the builders are adopted.

- Native AOT gate: Publish with /p:PublishAot=true and treat trimming warnings as errors per REFDISC-034..037.

## Testing Strategy
All test tiers must run against native AOT artifacts per REFDISC-034..037.


### Unit tests
- Cover option translation (keep-alive, idle timeout, stream/body limits) to ensure invalid settings throw the same exceptions previously produced by `GrpcInbound`/`HttpInbound`.
- Validate TLS/mTLS configuration helpers (certificate selector, client validation callback) for both builders to confirm required/optional modes behave identically.
- Exercise interceptor/middleware registration plumbing to guarantee duplicate registrations and null registries are handled gracefully.

### Integration tests
- Spin up hosts with HTTP/3 enabled and assert QUIC connections are accepted; repeat with QUIC disabled to confirm graceful downgrade to HTTP/2.
- Host REST endpoints, toggle HTTP/2/HTTP/1.1 policies, and verify mTLS enforcement and diagnostics availability.
- Ensure diagnostics endpoints (e.g., transport health, `/omnirelay/control/*`) remain reachable and expose the same data before and after adopting the builders.

### Feature tests
- Use OmniRelay.FeatureTests to run dispatcher + gossip roles simultaneously via the builders, ensuring both can be started/stopped independently without socket contention.
- Validate operator workflows (enable/disable HTTP/3, adjust HTTP limits) in the feature harness, confirming the builders apply the new settings without requiring code changes.

### Hyperscale Feature Tests
- Under OmniRelay.HyperscaleFeatureTests, start dozens of control-plane hosts using the builders, inject rolling restarts, and confirm QUIC stream limits, TLS renegotiation, and HTTP connection churn remain stable.
- Run stress scenarios alternating HTTP/3 availability (forcing downgrades) and thrashing HTTP middleware to ensure connection churn does not regress latency/throughput expectations.

## Implementation status
- `GrpcControlPlaneHostBuilder` + `HttpControlPlaneHostBuilder` are now exercised end-to-end by the dispatcher. When a `diagnostics.controlPlane` block is present the builder binds HTTP and gRPC URLs, plugs in the shared `TransportTlsManager`, and registers `DiagnosticsControlPlaneHost` / `LeadershipControlPlaneHost` in the dispatcher lifecycle so they start/stop cleanly with the data plane instead of piggybacking on user-facing inbounds. The leadership host implements `IGrpcServerInterceptorSink`, so every transport interceptor configured through `dispatcherOptions.GrpcInterceptors` flows automatically to the gRPC control-plane endpoints, preserving auth/logging behavior without ad-hoc wiring.

## References
- `src/OmniRelay/Transport/Grpc/GrpcInbound.cs` and `src/OmniRelay/Transport/Http/HttpInbound.cs` - current inbound logic to extract.
- `docs/architecture/service-discovery.md` - Control-plane transport requirements.
- REFDISC-034..037 - AOT readiness baseline and CI gating.



