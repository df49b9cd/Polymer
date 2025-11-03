# Middleware Composition

Polymer mirrors `yarpc-go` by layering transport-specific middleware stacks with procedure-level overrides. This guide explains how middleware is composed, how to attach per-procedure middleware using the builders introduced for parity, and what ordering guarantees the dispatcher provides.

## Global vs per-procedure middleware

- `DispatcherOptions` exposes distinct lists for inbound/outbound stacks across every RPC shape (unary, oneway, server/client streaming, duplex). These middleware run for every procedure registered on the dispatcher.
- Per-procedure middleware is appended after the global stack for that RPC type. Execution order is deterministic: middleware execute in the order they were registered, followed by the handler.
- Outbound middleware is layered on the `ClientConfiguration` returned by `Dispatcher.ClientConfig(service)`. Like inbound stacks, the order in the configuration reflects the order they were added.

```
// Execution order:
//   1) Global unary inbound (DispatcherOptions.UnaryInboundMiddleware)
//   2) Per-procedure unary middleware (builder.Use(...))
//   3) Handler
```

## Registering procedures with builders

`Dispatcher.Register*` overloads accept fluent builders so applications can attach middleware, aliases, encoding hints, and introspection metadata without manually constructing `ProcedureSpec` instances.

```csharp
var dispatcher = new Dispatcher(new DispatcherOptions("profile"));

dispatcher.RegisterUnary(
    "user::get",
    handler: async (request, ct) =>
    {
        var response = Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty);
        return await ValueTask.FromResult(Go.Ok(response));
    },
    configure: builder => builder
        .WithEncoding("json")
        .AddAliases(new[] { "v1::user::get" })
        .Use(new RpcMetricsMiddleware())
        .Use(new RpcLoggingMiddleware()));

dispatcher.RegisterStream(
    "events::subscribe",
    handler: (request, options, ct) =>
        ValueTask.FromResult(Go.Err<IStreamCall>(PolymerErrorAdapter.FromStatus(PolymerStatusCode.Unimplemented, "TODO"))),
    configure: builder => builder
        .WithMetadata(new StreamIntrospectionMetadata(
            new StreamChannelMetadata(StreamDirection.Server, "bounded-channel", Capacity: 100, TracksMessageCount: true)))
        .Use(new RpcTracingMiddleware()));
```

Available builders:

| Procedure Type  | Builder Type                    | Key Members |
|-----------------|---------------------------------|-------------|
| Unary           | `UnaryProcedureBuilder`         | `Handle`, `Use`, `WithEncoding`, `AddAlias` |
| Oneway          | `OnewayProcedureBuilder`        | `Handle`, `Use`, `WithEncoding`, `AddAlias` |
| Server Stream   | `StreamProcedureBuilder`        | `Handle`, `Use`, `WithEncoding`, `WithMetadata` |
| Client Stream   | `ClientStreamProcedureBuilder`  | `Handle`, `Use`, `WithEncoding`, `WithMetadata` |
| Duplex Stream   | `DuplexProcedureBuilder`        | `Handle`, `Use`, `WithEncoding`, `WithMetadata` |

> **Guard rails:** Builders require exactly one call to `Handle(...)`. Forgetting to set the handler triggers a descriptive exception so misconfigurations fail fast.

## Outbound middleware composition

- `DispatcherOptions.UnaryOutboundMiddleware`, `OnewayOutboundMiddleware`, etc. capture global client middleware.
- Per-service middleware can be layered on a `ClientConfiguration` by wrapping the returned outbounds or by composing additional middleware lists before constructing client helpers (e.g., `UnaryClient<TReq,TRes>`). A future configuration package will add higher-level helpers, matching `yarpc-go`'s config-driven approach.

## Testing middleware order

Unit tests can assert the new builder behaviour by combining recording middleware with the `Register*` overloads:

```csharp
var order = new List<string>();
var options = new DispatcherOptions("keyvalue");
options.UnaryInboundMiddleware.Add(new RecordingUnaryMiddleware("global", order));

dispatcher.RegisterUnary(
    "user::get",
    handler: (request, ct) =>
    {
        order.Add("handler");
        return ValueTask.FromResult(Go.Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
    },
    configure: builder => builder
        .Use(new RecordingUnaryMiddleware("procedure-1", order))
        .Use(new RecordingUnaryMiddleware("procedure-2", order)));

// Invoke → order == [ "global", "procedure-1", "procedure-2", "handler" ]
```

This ordering mirrors yarpc-go’s inbound pipeline semantics, ensuring middleware written for Go can be ported to .NET with minimal behavioural drift.

## Authoring custom middleware

### Unary inbound example

```csharp
public sealed class AuditUnaryMiddleware : IUnaryInboundMiddleware
{
    private readonly IAuditSink _sink;

    public AuditUnaryMiddleware(IAuditSink sink) => _sink = sink;

    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundDelegate next)
    {
        var start = TimeProvider.System.GetUtcNow();
        var result = await next(request, cancellationToken).ConfigureAwait(false);

        _sink.Record(new AuditEntry(
            request.Meta.Procedure ?? "unknown",
            success: result.IsSuccess,
            duration: TimeProvider.System.GetUtcNow() - start,
            status: result.IsFailure ? PolymerErrorAdapter.ToStatus(result.Error!) : PolymerStatusCode.OK));

        return result;
    }
}
```

- Always forward the call (`await next(...)`) to avoid breaking the pipeline.
- Preserve `CancellationToken` and propagate failures by returning the `Result` from downstream middleware.
- Use metadata from `request.Meta` to enrich logs (procedure, caller, transport).

### Unary outbound example

```csharp
public sealed class RetryBudgetMiddleware : IUnaryOutboundMiddleware
{
    private readonly RetryBudget _budget;

    public RetryBudgetMiddleware(RetryBudget budget) => _budget = budget;

    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundDelegate next)
    {
        if (!_budget.TryConsume())
        {
            return Go.Err<Response<ReadOnlyMemory<byte>>>(
                PolymerErrorAdapter.FromStatus(PolymerStatusCode.ResourceExhausted, "Retry budget depleted."));
        }

        var result = await next(request, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess || !PolymerErrors.IsRetryable(result.Error!))
        {
            _budget.ReturnToken();
        }

        return result;
    }
}
```

- Outbound middleware has access to `IRequest<ReadOnlyMemory<byte>>` and can inspect or mutate headers before invoking `next`.
- Use `PolymerErrors.IsRetryable` to decide whether to refund budgets or alter retry policies.

### Streaming best practices

Streaming middleware receives `StreamCallOptions` (direction, server/client) and access to `StreamCallContext` or `ClientStreamRequestContext`:

- Increment counters by hooking into channel operations (e.g., `await foreach` on `context.Requests.ReadAllAsync(...)`).
- Use `StreamCallContext.CompletedAtUtc` and `CompletionStatus` to tag metrics or logs once the stream finishes.
- Honor `StreamCallOptions.Direction` to avoid applying server-specific logic to client-initiated streams.

### General guidance

- Prefer stateless middleware and inject dependencies through constructors for testability.
- Surface critical metadata via headers or `ResponseMeta.WithHeader(...)`; the dispatcher merges metadata into transport responses.
- When working with channels, ensure writers/readers are completed to prevent leaking tasks—middleware should call `CompleteAsync`/`CompleteRequestsAsync` when short-circuiting.
- Keep middleware resilient: catch and translate unexpected exceptions with `PolymerErrors.FromException` so transports can emit canonical codes.
