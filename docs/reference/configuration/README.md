# Configuration Samples

Polymer configuration is layered using standard `IConfiguration` providers. The files in this folder illustrate a typical setup:

- `appsettings.json` defines base dispatcher wiring (service name, transports, middleware, and logging defaults).
- `appsettings.Development.json` overrides outbound addresses and logging levels for local development.
- `appsettings.Production.json` tightens logging and demonstrates attaching a custom peer chooser via the new `peer` spec block.

When hosted with `Host.CreateApplicationBuilder`, these files combine with environment variables automatically. Environment overrides follow the usual naming, for example:

```bash
DOTNET_ENVIRONMENT=Production \
polymer__outbounds__ledger__unary__grpc__0__addresses__0=http://ledger-c.internal:9090
```

See `PolymerConfigurationTests` for unit coverage that exercises custom specs and configuration layering.
