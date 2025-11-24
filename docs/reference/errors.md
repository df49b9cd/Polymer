# OmniRelay Error Handling

Guidance for surfacing and interpreting errors consistently across transports while maintaining parity with `yarpc-go`.

## Status & Metadata

- `OmniRelayException` wraps failures with a canonical `OmniRelayStatusCode`, the original `Hugo.Error`, and the transport that surfaced the issue.
- `OmniRelayErrorAdapter` annotates errors with:
  - `omnirelay.status`: string representation of the status code.
  - `omnirelay.faultType`: `Client` or `Server` classification (when known).
  - `omnirelay.retryable`: boolean hint to retry middleware and call sites.
  - `omnirelay.transport`: transport identifier (`http`, `grpc`, …).
- Helpers in `OmniRelayErrors` provide structured handling:
  - `OmniRelayErrors.FromException` → `OmniRelayException`.
  - `OmniRelayErrors.IsStatus` / `TryGetStatus`.
  - `OmniRelayErrors.GetFaultType` for quick classification.
  - `OmniRelayErrors.IsRetryable` to align with outbound retry policy.

## ASP.NET Core (HTTP)

Use `OmniRelayExceptionFilter` to normalize exceptions thrown by controllers, Razor pages, and minimal API endpoints:

```csharp
builder.Services.AddControllers(options =>
{
    options.AddOmniRelayExceptionFilter(); // transport defaults to "http"
});
```

Effects:

- Converts unhandled exceptions into `OmniRelayException`.
- Writes canonical headers (`Rpc-Status`, `Rpc-Error-Code`, `Rpc-Error-Message`, `Rpc-Transport`).
- Serializes the error payload (`message`, `status`, `code`, `metadata`) so HTTP clients receive the same shape as OmniRelay inbounds.

For minimal APIs, register the filter on the shared `MvcOptions` or wrap handlers with a try/catch that calls `OmniRelayErrors.FromException`.

## gRPC Services

Add `GrpcExceptionAdapterInterceptor` to the server to map thrown exceptions into canonical RPC trailers:

```csharp
builder.Services.AddGrpc(options =>
{
    options.Interceptors.Add<GrpcExceptionAdapterInterceptor>();
});
```

Benefits:

- Non-`RpcException` failures become `RpcException`s whose status matches `OmniRelayStatusCode`.
- Trailers include OmniRelay metadata (`omnirelay-status`, `omnirelay-error-code`, `omnirelay-transport`, fault/retry hints).
- Existing middleware and diagnostics that rely on trailers stay aligned with the HTTP transport.

If you already throw `RpcException` with OmniRelay trailers, the interceptor leaves the exception untouched.

## Client Patterns

- Always wrap outbound faults with `OmniRelayErrors.FromException` (the retry middleware performs this automatically).
- Use `OmniRelayErrors.IsRetryable(error)` before manual retries.
- Inspect `OmniRelayErrors.GetFaultType(exception)` for client/server attribution in logs.
- When emitting structured logs, include `OmniRelayException.Error.Metadata` to preserve fault details.

## Testing & Diagnostics

- Unit tests can assert metadata via `OmniRelayErrorAdapter.FaultMetadataKey` / `RetryableMetadataKey`.
- HTTP integration tests should verify the response headers/JSON mirror the filter output.
- gRPC tests should inspect response trailers for `omnirelay-status` and `omnirelay.retryable` to confirm adapter wiring.

## Migration Checklist

1. Register `OmniRelayExceptionFilter` for ASP.NET Core entry points.
2. Register `GrpcExceptionAdapterInterceptor` for gRPC services.
3. Ensure custom middleware rethrows `OmniRelayException` or wraps via `OmniRelayErrors.FromException`.
4. Update documentation/tooling references to include the new adapters.  
   (The parity backlog tracks this under **Error Model Parity → Error Helpers**.)

## Dispatcher Validation Error Codes

Dispatcher configuration and registration now surface validation failures via Hugo `Error.Code` values instead of throwing. Clients should bubble these codes (HTTP 400 / gRPC InvalidArgument unless noted).

| Code | Meaning | Typical Surface |
| --- | --- | --- |
| `dispatcher.codec.local_service_required` | `DispatcherOptions.ServiceName` missing/whitespace when building codec registry | Startup/config load |
| `dispatcher.codec.service_required` | Outbound codec registration missing remote service id | Startup/config load |
| `dispatcher.codec.procedure_required` | Codec registration missing procedure name | Startup/config load |
| `dispatcher.codec.duplicate` | Codec already registered for given scope/service/procedure/kind | Startup/config load |
| `dispatcher.codec.registration_failed` | Wrapper when codec registry creation fails for any reason | Startup/config load |
| `dispatcher.procedure.name_required` | Procedure name null/whitespace during registration | Runtime registration |
| `dispatcher.procedure.alias_invalid` | Alias null/whitespace during registration | Runtime registration |
| `dispatcher.procedure.handler_missing` | Builder lacks a handler when Build is invoked | Runtime registration |
| `dispatcher.config.service_required` | DispatcherOptions.Create service name missing | Config pipeline |
| `dispatcher.config.lifecycle_name_required` | Lifecycle component name missing | Config pipeline |

HTTP/gRPC gateways should map `dispatcher.*` codes to 400 (`NotFound` for resources is unaffected). Consumers can still opt into constructor-based paths (which throw) but should prefer `Dispatcher.Create` and Result-based builders for AOT-safe flows.

## ResourceLease Validation Error Codes

ResourceLease endpoints surface validation failures through `Error.Code` instead of throwing. Transport layers should map these to HTTP 400 / gRPC `InvalidArgument`.

| Code | Meaning | Typical Surface |
| --- | --- | --- |
| `resourcelease.payload.required` | Enqueue/restore payload missing | `resourcelease::enqueue`, `resourcelease::restore` |
| `resourcelease.payload.resource_type_required` | `ResourceType` missing/whitespace | `resourcelease::enqueue`, restore |
| `resourcelease.payload.resource_id_required` | `ResourceId` missing/whitespace | `resourcelease::enqueue`, restore |
| `resourcelease.payload.partition_key_required` | `PartitionKey` missing/whitespace | `resourcelease::enqueue`, restore |
| `resourcelease.payload.encoding_required` | `PayloadEncoding` missing/whitespace | `resourcelease::enqueue`, restore |
| `resourcelease.restore.pending_item_required` | Null entry in restore batch | `resourcelease::restore` |

Downstream replication/deterministic components continue to return structured `Error` values (e.g., `replication.stage`, `replication.eventType`) so callers can aggregate failures without exceptions.

### Deterministic coordinator / state-store wiring

| Code | Meaning | Typical Surface |
| --- | --- | --- |
| `resourcelease.deterministic.options_required` | `ResourceLeaseDeterministicOptions` missing | Deterministic coordinator creation |
| `resourcelease.deterministic.state_store_required` | `StateStore` missing on options | Deterministic coordinator creation |
| `resourcelease.deterministic.root_required` | Root path missing/whitespace for file-system state store | `FileSystemDeterministicStateStore.Create` |
| `resourcelease.dispatcher.dispatcher_required` | Dispatcher instance missing | `ResourceLeaseDispatcherComponent.Create` |
| `resourcelease.dispatcher.options_required` | Dispatcher options missing | `ResourceLeaseDispatcherComponent.Create` |
