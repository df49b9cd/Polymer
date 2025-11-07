# Minimal API Bridge Sample

This sample hosts ASP.NET Core Minimal APIs and an OmniRelay dispatcher inside the same Generic Host so HTTP-first teams can gradually introduce OmniRelay transports without rewriting every controller. REST endpoints and RPC procedures share the exact handler classes (`GreetingHandlers`, `PortfolioHandlers`) plus middleware, codecs, and logging already registered in the DI container.

## Local Topology

| Component | Address | Purpose |
| --- | --- | --- |
| Minimal API host | `http://127.0.0.1:5058` | Traditional REST endpoints (`/api/*`) that call handler services directly. |
| OmniRelay HTTP inbound | `http://127.0.0.1:7080` | `/yarpc/v1` for RPC clients, `/omnirelay/{healthz,readyz,introspect}` for ops tooling. |
| OmniRelay gRPC inbound | `http://127.0.0.1:7090` | gRPC unary traffic (JSON codec by default). |

Both hosts share the same `BridgeRuntime` singleton so the dispatcher life cycle follows the web app life cycle via `DispatcherHostedService`.

## Running the sample

```bash
dotnet run --project samples/MinimalApiBridge
```

Once running:

- Browse `http://127.0.0.1:5058/api/dispatcher` to see which transports are bound.
- Call REST endpoints:
  - `GET http://127.0.0.1:5058/api/greetings/{name}`
  - `POST http://127.0.0.1:5058/api/portfolios/{portfolioId}/rebalance`
  - `POST http://127.0.0.1:5058/api/alerts`

## OmniRelay procedures exposed

| Procedure | Shape | Codec | Handler |
| --- | --- | --- | --- |
| `greetings::say` (alias `greetings::hello`) | Unary | JSON | `GreetingHandlers.CreateGreetingAsync` |
| `portfolio::rebalance` | Unary | JSON | `PortfolioHandlers.CreatePlanAsync` |
| `alerts::emit` | Oneway | JSON | `PortfolioHandlers.RecordAlert` |

These procedures rely on the same handler classes that power the Minimal APIs, proving that legacy HTTP controllers and new OmniRelay callers can share one code path.

## OmniRelay CLI recipes

Run these in a separate terminal while the sample host is running.

### Inspect the dispatcher

```bash
omnirelay introspect --url http://127.0.0.1:7080/omnirelay/introspect --format text
```

### Issue HTTP unary calls

```bash
omnirelay request \
  --transport http \
  --url http://127.0.0.1:7080/yarpc/v1 \
  --service samples.minimal-api-bridge \
  --procedure greetings::say \
  --encoding application/json \
  --body '{"name":"Codex","channel":"cli"}'
```

### Issue gRPC unary calls

```bash
omnirelay request \
  --transport grpc \
  --address http://127.0.0.1:7090 \
  --service samples.minimal-api-bridge \
  --procedure greetings::say \
  --encoding application/json \
  --body '{"name":"CLI over gRPC"}'
```

### Fire oneway alerts

```bash
omnirelay request \
  --transport http \
  --url http://127.0.0.1:7080/yarpc/v1 \
  --service samples.minimal-api-bridge \
  --procedure alerts::emit \
  --encoding application/json \
  --body '{"severity":"warn","message":"shadow alert","correlationId":"demo"}'
```

The console logs emitted by `ConsoleLoggingMiddleware` (shared between inbound HTTP + gRPC) make it easy to see which transport handled each call.

## Technical highlights

- **Shared DI + lifetimes:** The dispatcher, handler classes, codecs, and middleware are registered once in `Program.cs`. Minimal APIs resolve the same services that OmniRelay handlers use, ensuring behavior stays consistent across transports.
- **Manual dispatcher wiring:** `BridgeProcedureRegistrar` shows how to register JSON unary/oneway procedures programmatically, including alias metadata and codec selection, while still benefiting from `DispatcherJsonExtensions`.
- **Hosted life cycle:** `DispatcherHostedService` keeps the OmniRelay life cycle aligned with ASP.NET Core’s start/stop events, which is critical when running a single process with mixed workloads.
- **REST-to-RPC migration path:** Because both stacks share handlers, teams can migrate endpoints gradually—new OmniRelay callers invoke the dispatcher while legacy HTTP callers continue to hit Minimal APIs until they are ready to switch.
