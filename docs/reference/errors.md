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
