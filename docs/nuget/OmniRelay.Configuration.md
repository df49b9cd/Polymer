# OmniRelay.Configuration

OmniRelay.Configuration binds dispatcher settings from `IConfiguration` sources and wires OmniRelay into ASP.NET Core's dependency injection infrastructure.

## Features

- `AddOmniRelayDispatcher` extension to register the dispatcher, transports, codecs, and middleware from configuration.
- Declarative models for inbounds, outbounds, peer choosers, and middleware stacks.
- Built-in support for OpenTelemetry exporters and runtime diagnostics toggles.
- Extensibility via `ICustomInboundSpec`, `ICustomOutboundSpec`, and `ICustomPeerChooserSpec`.

## Usage

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLogging();
builder.Services.AddOmniRelayDispatcher(builder.Configuration.GetSection("polymer"));

var app = builder.Build();
await app.RunAsync();
```

Configuration samples live under `docs/reference/configuration/`. Review `OmniRelayConfigurationTests` for advanced scenarios such as custom specs, schema validation, and codec customization.

