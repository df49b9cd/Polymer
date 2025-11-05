# Configuration Samples

OmniRelay configuration is layered using standard `IConfiguration` providers. The files in this folder illustrate a typical setup:

- `appsettings.json` defines base dispatcher wiring (service name, transports, middleware, and logging defaults).
- `appsettings.Development.json` overrides outbound addresses and logging levels for local development.
- `appsettings.Production.json` tightens logging and demonstrates attaching a custom peer chooser via the new `peer` spec block.

When hosted with `Host.CreateApplicationBuilder`, these files combine with environment variables automatically. Environment overrides follow the usual naming, for example:

```bash
DOTNET_ENVIRONMENT=Production \
polymer__outbounds__ledger__unary__grpc__0__addresses__0=http://ledger-c.internal:9090
```

See `OmniRelayConfigurationTests` for unit coverage that exercises custom specs and configuration layering.

## Enabling HTTP/3 for clients (outbounds)

gRPC outbounds can opt-in to HTTP/3 with per-endpoint runtime settings. Prefer enabling HTTP/3 together with a permissive HTTP version policy so the client will downgrade automatically when QUIC isn’t available.

Example: enable HTTP/3 for a gRPC outbound and request HTTP/3-or-higher per call.

```json
{
	"polymer": {
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
