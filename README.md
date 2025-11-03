# Polymer

## Oneway (Fire-and-Forget) Example

```csharp
var dispatcherOptions = new DispatcherOptions("billing");

// HTTP inbound hosting
dispatcherOptions.AddLifecycle(
    "http-inbound",
    new HttpInbound(new[] { "http://0.0.0.0:8080" }));

// HTTP outbound to downstream "audit" service
dispatcherOptions.AddOnewayOutbound(
    "audit",
    null,
    new HttpOutbound(
        httpClient: new HttpClient { BaseAddress = new Uri("http://audit:8080/") },
        requestUri: new Uri("http://audit:8080/")));

var dispatcher = new Dispatcher(dispatcherOptions);

// Register a fire-and-forget handler
dispatcher.Register(new OnewayProcedureSpec(
    "billing",
    "audit::record",
    async (request, ct) =>
    {
        // Decode the payload using your codec of choice
        var codec = new JsonCodec<AuditRecord, object>();
        var decode = codec.DecodeRequest(request.Body, request.Meta);
        if (decode.IsFailure)
        {
            return Go.Err<OnewayAck>(decode.Error!);
        }

        await auditStore.WriteAsync(decode.Value, ct);
        return Go.Ok(OnewayAck.Ack());
    }));

await dispatcher.StartAsync();

// Create a client to invoke the oneway procedure
var codec = new JsonCodec<AuditRecord, object>();
var client = dispatcher.CreateOnewayClient<AuditRecord>("audit", codec);

await client.CallAsync(
    Request<AuditRecord>.Create(
        new AuditRecord(/* ... */),
        new RequestMeta(service: "audit", procedure: "audit::record")));
```

## Configuration Bootstrap

`appsettings.json`

```json
{
  "polymer": {
    "service": "gateway",
    "inbounds": {
      "http": [
        { "urls": [ "http://0.0.0.0:8080" ] }
      ]
    },
    "outbounds": {
      "keyvalue": {
        "unary": {
          "http": [
            { "url": "http://keyvalue:8080", "key": "primary" }
          ]
        }
      }
    }
  }
}
```

`Program.cs`

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddLogging();
builder.Services.AddPolymerDispatcher(builder.Configuration.GetSection("polymer"));

var app = builder.Build();
await app.RunAsync();
```

### Extending Configuration

Register custom transports or peer choosers by adding DI implementations of `ICustomInboundSpec`, `ICustomOutboundSpec`, or `ICustomPeerChooserSpec`. Configuration entries under `inbounds:custom`, `outbounds:<service>:<rpcKind>:custom`, or `peer` reference the spec by name and supply additional settings. See `PolymerConfigurationTests` for working examples, and review the layered samples in `docs/reference/configuration` for multi-environment `appsettings*.json` layouts.

## CLI

Use the `polymer` CLI for quick validation, introspection, and smoke testing:

```bash
dotnet run --project src/Polymer.Cli -- config validate --config appsettings.json
dotnet run --project src/Polymer.Cli -- introspect --url http://127.0.0.1:8080/polymer/introspect
dotnet run --project src/Polymer.Cli -- request --transport http --url http://127.0.0.1:8080/yarpc/v1 --service echo --procedure echo::ping --body '{"message":"cli"}'
dotnet run --project src/Polymer.Cli -- request --url http://127.0.0.1:8080/yarpc/v1 --service echo --procedure echo::ping --profile json:pretty --body '{"message":"profiles"}'
dotnet run --project src/Polymer.Cli -- script run --file docs/reference/cli-scripts/echo-harness.json
```

See `docs/reference/cli.md` for profiles, protobuf automation, and installing `polymer` as a .NET global tool.
