# REFDISC-004 - Shared Interceptor & Middleware Registries

## Goal
Decouple the dispatcher's gRPC interceptor registries, HTTP middleware stacks, and diagnostics endpoint wiring into reusable registries so control-plane services can enable logging, tracing, auth, and `/omnirelay/control/*` routes without duplicating pipeline composition.

## Scope
- Move `GrpcClientInterceptorRegistry`, `GrpcServerInterceptorRegistry`, and HTTP middleware registration helpers into standalone packages with DI extensions.
- Provide builder APIs that let hosts register interceptors/middleware by type with ordering + scope guarantees identical to dispatcher behavior.
- Centralize diagnostics endpoint mapping so gRPC and HTTP hosts expose `/omnirelay/control/logging|tracing|lease-health|peers` with consistent authorization and serialization.
- Ensure registries integrate with the diagnostics runtime, telemetry module, and transport builders/factories.
- Document how dispatcher and standalone control-plane hosts compose the registries plus configuration-driven toggles.

## Requirements
1. **Parity with dispatcher** - Shared registries must preserve ordering, filtering, DI semantics, and short-circuit behavior currently used inside dispatcher pipelines.
2. **Configuration-driven** - Allow middleware/interceptors to be toggled via appsettings/environment (enable auth/logging/tracing) without code changes.
3. **Diagnostics surfacing** - `/omnirelay/control/*` endpoints must run under the shared registries with consistent authorization, payloads, and runtime toggle integration.
4. **Auth & extensibility** - Support plugging in shared authentication/authorization policies (mTLS identities, bearer tokens) and ensure new interceptors/middleware can be slotted without dispatcher dependencies.
5. **Telemetry integration** - Registries must emit the same meters/activity sources/logging output and honor runtime sampling/logging toggles supplied by the diagnostics runtime.

## Deliverables
- Shared interceptor + middleware registry packages with DI extensions.
- Dispatcher refactor to remove private registry implementations in favor of the shared packages.
- Control-plane hosts (gossip/leadership/bootstrap) updated to opt into interceptors, middleware, and diagnostics endpoints via the registries.
- Documentation describing registration patterns, diagnostics routes, configuration switches, and extension points.

## Acceptance Criteria
- Dispatcher gRPC/HTTP pipelines maintain behavior and ordering after migration, including error handling and auth enforcement.
- Control-plane services can enable logging/tracing/auth interceptors or middleware via configuration and observe parity with dispatcher endpoints.
- Diagnostics endpoints stay available and emit identical payloads regardless of host, including runtime toggles for logging/tracing.
- Registries guard against duplicate registration even when multiple hosts run in the same process, ensuring metrics/logging remain stable.
- Configuration changes propagate without restarts (e.g., enabling middleware, adjusting sampling) using shared options/diagnostics runtime.

- Native AOT gate: Publish with /p:PublishAot=true and treat trimming warnings as errors per REFDISC-034..037.

## Testing Strategy
All test tiers must run against native AOT artifacts per REFDISC-034..037.


### Unit tests
- Validate registry ordering/deduplication logic for gRPC interceptors and HTTP middleware stacks.
- Test configuration toggles to confirm interceptors/middleware register conditionally and respond to runtime updates.
- Ensure diagnostics endpoint handlers return expected payloads, respect auth hooks, and handle null dependencies gracefully.

### Integration tests
- Register logging/tracing/auth interceptors + middleware in control-plane hosts, execute RPCs/HTTP calls, and assert logs/spans materialize with expected metadata.
- Toggle configuration flags via options binding and verify interceptors/middleware enable/disable without restarts.
- Confirm diagnostics endpoints return identical JSON payloads across dispatcher and control-plane hosts under shared auth policies.

### Feature tests
- Within OmniRelay.FeatureTests, enable logging interceptors and middleware for dispatcher + gossip roles, then drive operator flows (log level changes, tracing toggles) to verify parity.
- Inject auth failures and ensure both gRPC and HTTP pipelines short-circuit requests consistently and emit expected logs/metrics.

### Hyperscale Feature Tests
- In OmniRelay.HyperscaleFeatureTests, deploy many hosts with registries enabled, ensuring telemetry volume stays within expectations and runtime toggles remain responsive.
- Stress diagnostics endpoints with concurrent requests to confirm routing + serialization remain efficient under load.

## Implementation status
- The gRPC side now lives under `src/OmniRelay/Transport/Grpc/Interceptors/` where `GrpcTransportInterceptorBuilder` produces `GrpcClientInterceptorRegistry`/`GrpcServerInterceptorRegistry` instances and composite interceptors that the dispatcher feeds into every inbound/outbound component via the `IGrpcInterceptorSink` interfaces. Control-plane gRPC hosts such as `src/OmniRelay/Core/Leadership/LeadershipControlPlaneHost.cs` simply implement `IGrpcServerInterceptorSink` to inherit the dispatcher’s logging/auth/tracing interceptors without any bespoke wiring.
- HTTP middleware follows the same pattern via `HttpOutboundMiddlewareBuilder` and `HttpOutboundMiddlewareRegistry` in `src/OmniRelay/Transport/Http/Middleware/`. `HttpOutbound`/`HttpInbound` (and the CLI tooling that scaffolds them) attach the registry-provided stacks so logging, tracing, auth, and rate limiting can be toggled through configuration across dispatcher and standalone services.
- Diagnostics hosts created through `src/OmniRelay/Core/Diagnostics/DiagnosticsControlPlaneHost.cs` reuse the registries when mapping `/omnirelay/control/*` endpoints, which keeps runtime logging/tracing toggles and authorization policies identical whether the traffic lands on the dispatcher, the dedicated diagnostics HTTP host, or the leadership gRPC host.

## References
- `src/OmniRelay/Transport/Grpc/Interceptors/` and `src/OmniRelay/Transport/Http/Middleware/` - Existing infrastructure to extract.
- `docs/architecture/service-discovery.md` - Observability + governance expectations.
- REFDISC-034..037 - AOT readiness baseline and CI gating.


