# Configuration & Hosting

## Manual Dispatcher Setup
- Use `DispatcherOptions` to register HTTP/gRPC inbounds, define unary/oneway outbounds, and attach middleware before creating a `Dispatcher` instance (see README snippet). The inbound automatically exposes `/omnirelay/introspect`, `/healthz`, `/readyz`.
- Middleware chains are layered via `options.UnaryInboundMiddleware.Add(...)`, and codecs (`JsonCodec<TReq,TRes>`) drive request/response serialization.

## Source-generated configuration
- Use `AddOmniRelayDispatcherFromConfig("appsettings.dispatcher.json")` to load dispatcher settings via the built-in configuration binding generator (trim/AOT safe).
- `DispatcherConfig` is the trimmed DTO; map to `DispatcherOptions` via `DispatcherConfigMapper` without reflection or `Type.GetType`.
- Custom middleware/interceptors/peer choosers must be registered in code (e.g., `DispatcherComponentRegistry.RegisterMiddleware<T>("logging")`) and referenced by key in config.

## Hosting Scenarios
- **Generic Host**: `builder.Services.AddOmniRelayDispatcherFromConfig("appsettings.dispatcher.json");` then call `await host.RunAsync();` to co-host dispatcher + app services.
- **Native AOT**: rely on source-generated binding plus `DispatcherConfigJsonContext` to avoid reflection; publish with `./eng/run-aot-publish.sh [rid] [Configuration]`.
- **Docker/CI**: `./eng/run-ci.sh` reproduces pipeline builds; `docker build -f docker/Dockerfile.hyperscale.ci .` runs hyperscale smoke tests inside containers.

## Security & Certificates
- TLS/secret loading helpers now live in code-first attributes and the dispatcher config mapper; avoid runtime type lookups.

## Control-plane Hosting
- `DiagnosticsControlPlaneHost` (within `src/OmniRelay/Core/Diagnostics`) spins up HTTP control endpoints (logging/tracing toggles, shard control, probes, chaos) using `HttpControlPlaneHostOptions`. Use configuration flags to enable/disable features per deployment.
