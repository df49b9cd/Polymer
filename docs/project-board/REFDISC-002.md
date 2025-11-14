# REFDISC-002 - Control-Plane Client Factories

## Goal
Provide reusable gRPC HTTP/3 and REST HTTP/2 client factories that encapsulate transport preferences, mTLS, retries, and middleware so control-plane components issue RPCs without duplicating `GrpcOutbound`/`HttpOutbound` internals.

## Scope
- Extract channel construction, `SocketsHttpHandler` tuning, retry policies, and TLS selection logic from `GrpcOutbound` and `HttpOutbound`.
- Create DI-friendly factories (`IGrpcControlPlaneClientFactory`, `IHttpControlPlaneClientFactory`) that return configured `GrpcChannel`/`HttpClient` instances keyed by endpoint metadata or named profiles.
- Support endpoint-level HTTP/3/HTTP/2 capability hints plus runtime overrides (force HTTP/2/HTTP/1.1) driven by health probes.
- Allow attaching shared interceptors/middleware (auth, tracing, rate limiting) through registries rather than bespoke handlers per client.
- Document recommended factory profiles (control-plane vs. data-plane) and configuration keys for operators.

## Requirements
1. **Protocol preferences** - gRPC factory must default to HTTP/3 with automatic fallback to HTTP/2 using `HttpVersionPolicy.RequestVersionOrLower`; HTTP factory must default to HTTP/2 with HTTP/1.1 fallback and expose switches to force a specific version.
2. **Client mTLS** - Both factories must load client certificates, support custom validation callbacks, and enforce revocation/thumbprint policies identical to existing outbound helpers.
3. **Runtime knobs** - Expose configuration for max message sizes, ping delays/timeouts, retry/backoff policies, connection limits, decompression, and per-endpoint overrides.
4. **Peer awareness** - Provide hooks for peer chooser + circuit breaker injection so service discovery utilities can balance across replicas and shed load consistently.
5. **Observability & middleware** - Emit the current transport metrics (`grpc_outbound_*`, `omnirelay_transport_http_*`), tracing spans, and allow stacking shared interceptors/middleware via DI.

## Deliverables
- Factory interfaces + implementations under `OmniRelay.Transport.Grpc` and `OmniRelay.Transport.Http`.
- Refactoring of `GrpcOutbound`/`HttpOutbound` to consume the factories (removing duplicate handler setup).
- Updates to gossip/leadership/service-discovery agents to resolve the factories rather than newing `HttpClient`/`GrpcChannel`.
- Documentation outlining configuration keys, recommended profiles, and migration guidance.

## Acceptance Criteria
- Dispatcher outbound calls continue working via the refactored outbound helpers.
- Control-plane agents can dial peers over gRPC HTTP/3 and HTTP/2/HTTP/1.1 using the factories without referencing dispatcher classes.
- Client mTLS failures surface as actionable errors and never silently downgrade to insecure transport.
- Telemetry dashboards observe unchanged metrics, and middleware/interceptors fire consistently across dispatcher + control-plane calls.
- Endpoint hints to disable HTTP/3 or HTTP/2 for specific peers take effect without restarting the process.

- Native AOT gate: Publish with /p:PublishAot=true and treat trimming warnings as errors per REFDISC-034..037.

## Testing Strategy
All test tiers must run against native AOT artifacts per REFDISC-034..037.


### Unit tests
- Validate handler creation under combinations of TLS settings, HTTP version policies, message size limits, retries, and decompression toggles.
- Exercise peer chooser + circuit breaker injection using fake endpoints to ensure round-robin, preferred-peer, and degraded modes behave.
- Confirm middleware/interceptor stacking honors ordering rules and handles duplicate registrations gracefully.

### Integration tests
- Dial test endpoints over HTTP/3, HTTP/2, and HTTP/1.1 to verify negotiation/fallback plus mTLS enforcement.
- Toggle runtime settings (timeouts, retry limits, HTTP version) via configuration reload and confirm clients adapt.
- Ensure telemetry counters/spans produced by outbound traffic remain unchanged relative to existing dispatcher behavior.

### Feature tests
- Use OmniRelay.FeatureTests to run dispatcher + gossip roles simultaneously with the factories, driving control-plane traffic through both protocols and validating auth/logging hooks.
- Simulate endpoint-level HTTP/3 disablement plus circuit-breaker trips to ensure peer selection + retries behave as expected.

### Hyperscale Feature Tests
- Under OmniRelay.HyperscaleFeatureTests, run large node counts generating sustained control-plane traffic using the factories to ensure handler pooling, retries, and TLS renegotiation stay healthy.
- Stress configuration toggles (switch HTTP versions, rotate certificates) to ensure factories recycle handlers without leaks.

## Implementation status
- The factories now back real tooling. `DiagnosticsControlPlaneHost` and `LeadershipControlPlaneHost` resolve the HTTP/gRPC builders through the shared factories so their handlers inherit the same HTTP/3 preferences, TLS policies, and middleware stacks as dispatcher-owned transports. The `omnirelay mesh` CLI provisions those factories through the built-in options pipeline (no more ad-hoc monitors), so every control-plane request—including the live leadership watch via gRPC—flows through `IHttpControlPlaneClientFactory`/`IGrpcControlPlaneClientFactory` and picks up HTTP version negotiation, retry, and TLS defaults automatically.

## References
- `src/OmniRelay/Transport/Grpc/GrpcOutbound.cs` and `src/OmniRelay/Transport/Http/HttpOutbound.cs` - current outbound logic to extract.
- `docs/architecture/service-discovery.md` - Control-plane transport requirements.
- REFDISC-034..037 - AOT readiness baseline and CI gating.
