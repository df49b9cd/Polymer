# Streaming RPCs

OmniRelay exposes first-class streaming pipelines that mirror YARPC-Go’s server, client, and bidirectional RPC types. This guide walks through the public APIs you use to register streaming handlers and issue calls from clients. Every transport (currently HTTP SSE/websocket and gRPC) implements the same abstractions so application code stays transport-agnostic.

## Streaming Shapes

| RPC type | Handler delegate | Return type | Description |
| -------- | ---------------- | ----------- | ----------- |
| Server streaming | `StreamInboundDelegate` | `Result<IStreamCall>` | Client sends a single request; server pushes zero or more responses. |
| Client streaming | `ClientStreamInboundDelegate` | `Result<Response<ReadOnlyMemory<byte>>>` | Client sends a stream; server eventually replies with one response. |
| Bidirectional | `DuplexInboundDelegate` | `Result<IDuplexStreamCall>` | Client and server stream concurrently in both directions. |

Outbound helpers mirror the same shapes through `StreamClient<TReq,TRes>`, `ClientStreamClient<TReq,TRes>`, and `DuplexStreamClient<TReq,TRes>`.

## Registering Server-Streaming Handlers

Use `StreamProcedureSpec` to register a handler. The handler receives the serialized request and a `StreamCallOptions` description, and must return an `IStreamCall`. Transports provide helper factories that already wire the response channel to the concrete protocol.

```csharp
dispatcher.Register(new StreamProcedureSpec(
    service: "telemetry",
    name: "telemetry::tail",
    handler: (request, callOptions, cancellationToken) =>
    {
        var decode = codec.DecodeRequest(request.Body, request.Meta);
        if (decode.IsFailure)
        {
            return ValueTask.FromResult(Err<IStreamCall>(decode.Error!));
        }

        var call = GrpcServerStreamCall.Create(request.Meta, new ResponseMeta(encoding: "application/json"));

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in ReadEventsAsync(decode.Value, cancellationToken))
                {
                    var encode = codec.EncodeResponse(evt, call.ResponseMeta);
                    if (encode.IsFailure)
                    {
                        await call.CompleteAsync(encode.Error!, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await call.WriteAsync(encode.Value, cancellationToken).ConfigureAwait(false);
                }

                await call.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var error = OmniRelayErrors.FromException(ex, call.RequestMeta.Transport ?? "grpc");
                await call.CompleteAsync(error, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken);

        return ValueTask.FromResult(Ok<IStreamCall>(call));
    }));
```

`GrpcServerStreamCall.Create` returns an `IStreamCall` backed by gRPC response writers. For HTTP transports use `HttpStreamCall.CreateServerStream`. The `StreamCallContext` accessible via `call.Context` tracks message counts, completion status, and timestamps for observability and middleware.

## Building Client-Streaming Handlers

A client-streaming handler consumes a `ClientStreamRequestContext`. The request channel yields serialized frames; your handler must decode, apply business logic, and eventually return a unary response. Use `ClientStreamProcedureSpec` to register the handler.

```csharp
dispatcher.Register(new ClientStreamProcedureSpec(
    service: "analytics",
    name: "analytics::aggregate",
    handler: async (context, cancellationToken) =>
    {
        var accumulator = new Aggregate();

        await foreach (var payload in context.Requests.ReadAllAsync(cancellationToken))
        {
            var decode = codec.DecodeRequest(payload, context.Meta);
            if (decode.IsFailure)
            {
                return Err<Response<ReadOnlyMemory<byte>>>(decode.Error!);
            }

            accumulator.Add(decode.Value);
        }

        var responseMeta = new ResponseMeta(encoding: "application/json");
        var encode = codec.EncodeResponse(accumulator.ToResult(), responseMeta);
        return encode.IsSuccess
            ? Ok(Response<ReadOnlyMemory<byte>>.Create(encode.Value, responseMeta))
            : Err<Response<ReadOnlyMemory<byte>>>(encode.Error!);
    }));
```

The dispatcher hydrates middleware from `DispatcherOptions.ClientStreamInboundMiddleware`, so tracing, metrics, rate limiting, and retries apply automatically.

## Implementing Bidirectional Streaming

Bidirectional procedures registered via `DuplexProcedureSpec` return an `IDuplexStreamCall`. The call exposes reader/writer channel pairs for requests and responses so each side can operate independently.

```csharp
dispatcher.Register(new DuplexProcedureSpec(
    service: "chat",
    name: "chat::room",
    handler: (request, cancellationToken) =>
    {
        var call = DuplexStreamCall.Create(request.Meta, new ResponseMeta(encoding: "application/json"));

        _ = Task.Run(async () =>
        {
            await foreach (var payload in call.RequestReader.ReadAllAsync(cancellationToken))
            {
                var decode = codec.DecodeRequest(payload, request.Meta);
                if (decode.IsFailure)
                {
                    await call.CompleteResponsesAsync(decode.Error!, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var broadcast = codec.EncodeResponse(new ChatEvent(decode.Value), call.ResponseMeta);
                if (broadcast.IsFailure)
                {
                    await call.CompleteResponsesAsync(broadcast.Error!, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await call.ResponseWriter.WriteAsync(broadcast.Value, cancellationToken).ConfigureAwait(false);
            }

            await call.CompleteResponsesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

        return ValueTask.FromResult(Ok<IDuplexStreamCall>(call));
    }));
```

The `DuplexStreamCallContext` (reachable via `call.Context`) records request and response counters, completion reasons, and timestamps so middleware and observability tooling can report stream health.

## Calling Streaming Procedures

Clients are created from a running dispatcher through extension helpers:

```csharp
var streamClient = dispatcher.CreateStreamClient<TickerRequest, TickerUpdate>("telemetry", codec);
await foreach (var update in streamClient.CallAsync(request, new StreamCallOptions(StreamDirection.Server), ct))
{
    Console.WriteLine(update.Body.Symbol);
}

var clientStream = dispatcher.CreateClientStreamClient<MetricSample, Aggregate>("analytics", codec);
await using var session = await clientStream.StartAsync(request.Meta, ct);
await session.WriteAsync(sample1, ct);
await session.WriteAsync(sample2, ct);
await session.CompleteAsync(ct);
var result = await session.Response;

var duplexClient = dispatcher.CreateDuplexStreamClient<ChatMessage, ChatEvent>("chat", codec);
await using var chat = await duplexClient.StartAsync(request.Meta, ct);
await chat.WriteAsync(new ChatMessage("hello"), ct);
await foreach (var evt in chat.ReadResponsesAsync(ct))
{
    Console.WriteLine(evt.Body.Text);
}
```

- `StreamCallOptions` identifies the stream direction and flows through middleware.
- Client helpers automatically compose outbound middleware (`IStreamOutboundMiddleware`, `IClientStreamOutboundMiddleware`, `IDuplexOutboundMiddleware`) and translate codec errors into `OmniRelayException`.

## Metadata, Deadlines, and Completion

- `RequestMeta` carries caller, service, procedure, encoding, TTL, and deadline fields. Transports convert TTL/deadline into native timeouts.
- `ResponseMeta` is mutable; set headers or encoding before writing the first response.
- Call `CompleteAsync`, `CompleteRequestsAsync`, or `CompleteResponsesAsync` when streams finish. Passing an `Error` propagates canonical status codes to the transport. If you drop the call without completing, the dispatcher records a cancelled completion.
- Middleware can inspect `StreamCallContext` / `DuplexStreamCallContext` to log message counts, completion state, or propagate trailer metadata.

## Related Reading

- `tests/OmniRelay.Tests/Transport/GrpcTransportTests.cs` includes end-to-end fixtures for every streaming shape.
- `docs/todo.md` tracks remaining parity items such as compression negotiation for gRPC streams.
- `docs/reference/diagnostics.md` documents the metrics emitted by streaming pipelines (message counters, durations, failure reasons).
