# Configuration Samples

OmniRelay configuration is layered using standard `IConfiguration` providers. The files in this folder illustrate a typical setup:

- `appsettings.json` defines base dispatcher wiring (service name, transports, middleware, and logging defaults).
- `appsettings.Development.json` overrides outbound addresses and logging levels for local development.
- `appsettings.Production.json` tightens logging and demonstrates attaching a custom peer chooser via the new `peer` spec block.

When hosted with `Host.CreateApplicationBuilder`, these files combine with environment variables automatically. Environment overrides follow the usual naming, for example:

```bash
DOTNET_ENVIRONMENT=Production \
omnirelay__outbounds__ledger__unary__grpc__0__addresses__0=http://ledger-c.internal:9090
```

See `OmniRelayConfigurationTests` for unit coverage that exercises custom specs and configuration layering.

## Failure handling

Dispatcher creation is now fully result-based. `DispatcherConfigMapper.CreateDispatcher` returns `Result<Dispatcher>` so misconfiguration (invalid mode, malformed URLs, missing TLS files, etc.) is surfaced as structured errors with metadata instead of throwing. The DI helpers (`AddOmniRelayDispatcherFromConfiguration` / `AddOmniRelayDispatcherFromConfig`) still fail fast by calling `ValueOrThrow()` on the result, ensuring host startup aborts with the recorded error details. When you consume the mapper directly, prefer `Result.Match`/`Result.Then` to keep configuration errors in the Hugo pipeline.

## Enabling HTTP/3 for clients (outbounds)

gRPC outbounds can opt-in to HTTP/3 with per-endpoint runtime settings. Prefer enabling HTTP/3 together with a permissive HTTP version policy so the client will downgrade automatically when QUIC isn’t available.

Example: enable HTTP/3 for a gRPC outbound and request HTTP/3-or-higher per call.

```json
{
 "omnirelay": {
  "outbounds": {
   "ledger": {
    "unary": {
     "grpc": [
      {
       "addresses": [ "https://ledger.internal:9091" ],
       "remoteService": "ledger",
       "runtime": {
        "enableHttp3": true,
        "requestVersion": "3.0",
        "versionPolicy": "request-version-or-higher",
        "keepAlivePingDelay": "00:00:45",
        "keepAlivePingTimeout": "00:00:10"
       }
      }
     ]
    }
   }
  }
 }
}
```

Notes:

- HTTP/3 requires HTTPS endpoints and TLS 1.3-capable certificates. The configuration binder will reject non-HTTPS addresses when `enableHttp3` is true.
- Behind the scenes the client sets SocketsHttpHandler.EnableMultipleHttp3Connections and adds a delegating handler that applies `RequestVersion = 3.0` and `VersionPolicy = RequestVersionOrHigher` so calls retain HTTP/2/1.1 compatibility.
- For high concurrency, consider tuning keep-alive pings to keep connections warm and reduce cold-start latency on idle pools.

### HTTP outbound example

Enable HTTP/3 for `HttpOutbound` with a permissive version policy to allow fallback when QUIC isn’t available:

```json
{
 "omnirelay": {
  "outbounds": {
   "audit": {
    "oneway": {
     "http": [
      {
       "url": "https://audit.internal:8443/yarpc/v1/audit::record",
       "runtime": {
        "enableHttp3": true,
        "requestVersion": "3.0",
        "versionPolicy": "request-version-or-higher"
       }
      }
     ]
    }
   }
  }
 }
}
```

The configuration binder enforces HTTPS when `enableHttp3` is true. Requests set `Version=3.0` and `VersionPolicy=RequestVersionOrHigher` so existing HTTP/2/1.1 endpoints continue to work.
