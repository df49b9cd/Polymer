# Configuration & Hosting

## Manual Dispatcher Setup
- Use `DispatcherOptions` to register HTTP/gRPC inbounds, define unary/oneway outbounds, and attach middleware before creating a `Dispatcher` instance (see README snippet). The inbound automatically exposes `/omnirelay/introspect`, `/healthz`, `/readyz`.
- Middleware chains are layered via `options.UnaryInboundMiddleware.Add(...)`, and codecs (`JsonCodec<TReq,TRes>`) drive request/response serialization.

## Configuration Binder (`src/OmniRelay.Configuration`)
- `AddOmniRelayDispatcher` wires an OmniRelay dispatcher into Generic Host using strongly-typed options loaded from `appsettings.json`.
- Configuration sections cover service identity, inbound/outbound transport definitions, middleware stacks, peer choosers, and security policies.
- Extension points (`ICustomInboundSpec`, `ICustomOutboundSpec`, `ICustomPeerChooserSpec`) let packages register custom transports or routing logic.

## Hosting Scenarios
- **Generic Host**: `builder.Services.AddOmniRelayDispatcher(builder.Configuration);` then call `await host.RunAsync();` to co-host dispatcher + app services.
- **Native AOT**: set `omnirelay:nativeAot:enabled: true` in configuration to use the trimming-safe bootstrapper (strict by default, only registered middleware/interceptors allowed), or use the reflection-free overload `AddOmniRelayDispatcherAot(options, (sp, dispatcher) => { /* register codecs/transports */ });`. Follow `docs/architecture/aot-guidelines.md` and run `./eng/run-aot-publish.sh [rid] [Configuration]` to produce self-contained binaries.
- **Docker/CI**: `./eng/run-ci.sh` reproduces pipeline builds; `docker build -f docker/Dockerfile.hyperscale.ci .` runs hyperscale smoke tests inside containers.

## Security & Certificates
- Configuration binder handles TLS via `WorkloadIdentity`/`Bootstrap` sections. Helpers load certs, SPIFEE identities, and bootstrap tokens (see `ServiceCollectionExtensions.cs`).

## Control-plane Hosting
- `DiagnosticsControlPlaneHost` (within `src/OmniRelay/Core/Diagnostics`) spins up HTTP control endpoints (logging/tracing toggles, shard control, probes, chaos) using `HttpControlPlaneHostOptions`. Use configuration flags to enable/disable features per deployment.
