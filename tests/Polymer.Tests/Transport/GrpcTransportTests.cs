using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Dispatcher;
using Polymer.Transport.Grpc;
using Polymer.Errors;
using Xunit;
using static Hugo.Go;

namespace Polymer.Tests.Transport;

public class GrpcTransportTests
{
    static GrpcTransportTests()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
    }

    private const string TransportName = "grpc";
    private const string EncodingHeaderKey = "rpc-encoding";
    private const string StatusTrailerKey = "polymer-status";
    private const string EncodingTrailerKey = "polymer-encoding";
    private const string ErrorMessageTrailerKey = "polymer-error-message";
    private const string ErrorCodeTrailerKey = "polymer-error-code";

    [Fact(Timeout = 30_000)]
    public async Task ServerStreaming_OverGrpcTransport()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("stream");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "stream");
        options.AddStreamOutbound("stream", null, grpcOutbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        await using var serverTasks = new ServerTaskTracker();

        dispatcher.Register(new StreamProcedureSpec(
            "stream",
            "stream::events",
            (request, callOptions, cancellationToken) =>
            {
                var decode = codec.DecodeRequest(request.Body, request.Meta);
                if (decode.IsFailure)
                {
                    return ValueTask.FromResult(Err<IStreamCall>(decode.Error!));
                }

                var streamCall = GrpcServerStreamCall.Create(request.Meta, new ResponseMeta(encoding: "application/json"));

                var backgroundTask = Task.Run(async () =>
                {
                    try
                    {
                        for (var i = 0; i < 3; i++)
                        {
                            var response = new EchoResponse { Message = $"event-{i}" };
                            var encode = codec.EncodeResponse(response, streamCall.ResponseMeta);
                            if (encode.IsFailure)
                            {
                                await streamCall.CompleteAsync(encode.Error!).ConfigureAwait(false);
                                return;
                            }

                            await streamCall.WriteAsync(encode.Value, cancellationToken).ConfigureAwait(false);
                            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        await streamCall.CompleteAsync().ConfigureAwait(false);
                    }
                }, cancellationToken);
                serverTasks.Track(backgroundTask);

                return ValueTask.FromResult(Ok<IStreamCall>(streamCall));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateStreamClient<EchoRequest, EchoResponse>("stream", codec);
            var requestMeta = new RequestMeta(
                service: "stream",
                procedure: "stream::events",
                encoding: "application/json",
                transport: "grpc");
            var request = new Request<EchoRequest>(requestMeta, new EchoRequest("seed"));

            var responses = new List<string>();
            await foreach (var response in client.CallAsync(request, new StreamCallOptions(StreamDirection.Server), ct))
            {
                responses.Add(response.Body.Message);
            }

            Assert.Equal(new[] { "event-0", "event-1", "event-2" }, responses);
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ServerStreaming_ErrorMidStream_PropagatesToClient()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("stream");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "stream");
        options.AddStreamOutbound("stream", null, grpcOutbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        await using var serverTasks = new ServerTaskTracker();

        dispatcher.Register(new StreamProcedureSpec(
            "stream",
            "stream::fails",
            (request, callOptions, cancellationToken) =>
            {
                var decode = codec.DecodeRequest(request.Body, request.Meta);
                if (decode.IsFailure)
                {
                    return ValueTask.FromResult(Err<IStreamCall>(decode.Error!));
                }

                var streamCall = GrpcServerStreamCall.Create(
                    request.Meta,
                    new ResponseMeta(encoding: "application/json"));

                var background = Task.Run(async () =>
                {
                    try
                    {
                        var first = codec.EncodeResponse(new EchoResponse { Message = "first" }, streamCall.ResponseMeta);
                        if (first.IsFailure)
                        {
                            await streamCall.CompleteAsync(first.Error!, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        await streamCall.WriteAsync(first.Value, cancellationToken).ConfigureAwait(false);

                        var error = PolymerErrorAdapter.FromStatus(
                            PolymerStatusCode.Internal,
                            "stream failure",
                            transport: TransportName);
                        await streamCall.CompleteAsync(error, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await streamCall.CompleteAsync(PolymerErrorAdapter.FromStatus(
                            PolymerStatusCode.Internal,
                            ex.Message ?? "unexpected failure",
                            transport: TransportName), cancellationToken).ConfigureAwait(false);
                    }
                }, cancellationToken);

                serverTasks.Track(background);

                return ValueTask.FromResult(Ok<IStreamCall>(streamCall));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateStreamClient<EchoRequest, EchoResponse>("stream", codec);
            var requestMeta = new RequestMeta(
                service: "stream",
                procedure: "stream::fails",
                encoding: "application/json",
                transport: "grpc");
            var request = new Request<EchoRequest>(requestMeta, new EchoRequest("payload"));

            var enumerator = client.CallAsync(request, new StreamCallOptions(StreamDirection.Server), ct)
                .GetAsyncEnumerator(ct);

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("first", enumerator.Current.Body.Message);

            var exception = await Assert.ThrowsAsync<PolymerException>(async () =>
            {
                await enumerator.MoveNextAsync();
            });

            await enumerator.DisposeAsync();

            Assert.Equal(PolymerStatusCode.Internal, exception.StatusCode);
            Assert.Contains("stream failure", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ClientStreaming_OverGrpcTransport()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("stream");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "stream");
        options.AddClientStreamOutbound("stream", null, grpcOutbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<AggregateChunk, AggregateResponse>(encoding: "application/json");

        dispatcher.Register(new ClientStreamProcedureSpec(
            "stream",
            "stream::aggregate",
            async (context, cancellationToken) =>
            {
                var totalBytes = 0;
                var reader = context.Requests;

                while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var payload))
                    {
                        var decodeResult = codec.DecodeRequest(payload, context.Meta);
                        if (decodeResult.IsFailure)
                        {
                            return Err<Response<ReadOnlyMemory<byte>>>(decodeResult.Error!);
                        }

                        totalBytes += decodeResult.Value.Amount;
                    }
                }

                var aggregateResponse = new AggregateResponse(totalBytes);
                var responseMeta = new ResponseMeta(encoding: codec.Encoding);
                var encodeResponse = codec.EncodeResponse(aggregateResponse, responseMeta);
                if (encodeResponse.IsFailure)
                {
                    return Err<Response<ReadOnlyMemory<byte>>>(encodeResponse.Error!);
                }

                var response = Response<ReadOnlyMemory<byte>>.Create(encodeResponse.Value, responseMeta);
                return Ok(response);
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateClientStreamClient<AggregateChunk, AggregateResponse>("stream", codec);

            var requestMeta = new RequestMeta(
                service: "stream",
                procedure: "stream::aggregate",
                encoding: codec.Encoding,
                transport: "grpc");

            await using var stream = await client.StartAsync(requestMeta, ct);

            await stream.WriteAsync(new AggregateChunk(Amount: 2), ct);
            await stream.WriteAsync(new AggregateChunk(Amount: 5), ct);
            await stream.CompleteAsync(ct);

            var response = await stream.Response;

            Assert.Equal(7, response.Body.TotalAmount);
            Assert.Equal(codec.Encoding, stream.ResponseMeta.Encoding);
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ClientStreaming_CancellationFromClient()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("stream");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "stream");
        options.AddClientStreamOutbound("stream", null, grpcOutbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<AggregateChunk, AggregateResponse>(encoding: "application/json");
        dispatcher.Register(new ClientStreamProcedureSpec(
            "stream",
            "stream::aggregate",
            async (context, cancellationToken) =>
            {
                await foreach (var _ in context.Requests.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    // Simply drain until cancellation.
                }

                return Err<Response<ReadOnlyMemory<byte>>>(PolymerErrorAdapter.FromStatus(
                    PolymerStatusCode.Cancelled,
                    "cancelled"));
            }));

        var cts = new CancellationTokenSource();
        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateClientStreamClient<AggregateChunk, AggregateResponse>("stream", codec);
            var requestMeta = new RequestMeta(service: "stream", procedure: "stream::aggregate", encoding: codec.Encoding, transport: "grpc");

            await using var stream = await client.StartAsync(requestMeta, ct);

            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await stream.WriteAsync(new AggregateChunk(Amount: 1), cts.Token);
            });
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ClientStreaming_DeadlineExceededMapsStatus()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("stream");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "stream");
        options.AddClientStreamOutbound("stream", null, grpcOutbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<AggregateChunk, AggregateResponse>(encoding: "application/json");

        dispatcher.Register(new ClientStreamProcedureSpec(
            "stream",
            "stream::deadline",
            async (context, cancellationToken) =>
            {
                Assert.True(context.Meta.Deadline.HasValue);
                await foreach (var _ in context.Requests.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                }

                return Err<Response<ReadOnlyMemory<byte>>>(PolymerErrorAdapter.FromStatus(
                    PolymerStatusCode.DeadlineExceeded,
                    "deadline exceeded"));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateClientStreamClient<AggregateChunk, AggregateResponse>("stream", codec);
            var requestMeta = new RequestMeta(
                service: "stream",
                procedure: "stream::deadline",
                encoding: codec.Encoding,
                transport: "grpc",
                deadline: DateTimeOffset.UtcNow.AddMilliseconds(200));

            await using var stream = await client.StartAsync(requestMeta, ct);
            await stream.CompleteAsync(ct);

            var exception = await Assert.ThrowsAsync<PolymerException>(async () => await stream.Response);
            Assert.Equal(PolymerStatusCode.DeadlineExceeded, exception.StatusCode);
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ClientStreaming_LargePayloadChunks()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("stream");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "stream");
        options.AddClientStreamOutbound("stream", null, grpcOutbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<AggregateChunk, AggregateResponse>(encoding: "application/json");

        dispatcher.Register(new ClientStreamProcedureSpec(
            "stream",
            "stream::huge",
            async (context, cancellationToken) =>
            {
                var total = 0;
                await foreach (var payload in context.Requests.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    var decode = codec.DecodeRequest(payload, context.Meta);
                    if (decode.IsFailure)
                    {
                        return Err<Response<ReadOnlyMemory<byte>>>(decode.Error!);
                    }

                    total += decode.Value.Amount;
                }

                var response = new AggregateResponse(total);
                var responseMeta = new ResponseMeta(encoding: codec.Encoding);
                var encode = codec.EncodeResponse(response, responseMeta);
                if (encode.IsFailure)
                {
                    return Err<Response<ReadOnlyMemory<byte>>>(encode.Error!);
                }

                return Ok(Response<ReadOnlyMemory<byte>>.Create(encode.Value, responseMeta));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateClientStreamClient<AggregateChunk, AggregateResponse>("stream", codec);
            var requestMeta = new RequestMeta(service: "stream", procedure: "stream::huge", encoding: codec.Encoding, transport: "grpc");

            await using var stream = await client.StartAsync(requestMeta, ct);

            const int chunkCount = 1_000;
            for (var i = 0; i < chunkCount; i++)
            {
                await stream.WriteAsync(new AggregateChunk(1), ct);
            }

            await stream.CompleteAsync(ct);

            var response = await stream.Response;
            Assert.Equal(chunkCount, response.Body.TotalAmount);
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ClientStreaming_ServerErrorPropagatesToClient()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("stream");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "stream");
        options.AddClientStreamOutbound("stream", null, grpcOutbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<AggregateChunk, AggregateResponse>(encoding: "application/json");

        dispatcher.Register(new ClientStreamProcedureSpec(
            "stream",
            "stream::server-error",
            async (context, cancellationToken) =>
            {
                var chunks = 0;
                await foreach (var payload in context.Requests.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    var decode = codec.DecodeRequest(payload, context.Meta);
                    if (decode.IsFailure)
                    {
                        return Err<Response<ReadOnlyMemory<byte>>>(decode.Error!);
                    }

                    chunks += decode.Value.Amount;
                    if (chunks >= 1)
                    {
                        return Err<Response<ReadOnlyMemory<byte>>>(PolymerErrorAdapter.FromStatus(
                            PolymerStatusCode.Unavailable,
                            "service unavailable",
                            transport: TransportName));
                    }
                }

                return Err<Response<ReadOnlyMemory<byte>>>(PolymerErrorAdapter.FromStatus(
                    PolymerStatusCode.Internal,
                    "no data received",
                    transport: TransportName));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateClientStreamClient<AggregateChunk, AggregateResponse>("stream", codec);
            var requestMeta = new RequestMeta(
                service: "stream",
                procedure: "stream::server-error",
                encoding: codec.Encoding,
                transport: "grpc");

            await using var stream = await client.StartAsync(requestMeta, ct);

            await stream.WriteAsync(new AggregateChunk(Amount: 1), ct);
            await stream.CompleteAsync(ct);

            var exception = await Assert.ThrowsAsync<PolymerException>(async () => await stream.Response);
            Assert.Equal(PolymerStatusCode.Unavailable, exception.StatusCode);
            Assert.Contains("service unavailable", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task UnaryRoundtrip_OverGrpcTransport()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("echo");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "echo");
        options.AddUnaryOutbound("echo", null, grpcOutbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        dispatcher.Register(new UnaryProcedureSpec(
            "echo",
            "ping",
            (request, cancellationToken) =>
            {
                var decodeResult = codec.DecodeRequest(request.Body, request.Meta);
                if (decodeResult.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(decodeResult.Error!));
                }

                var responsePayload = new EchoResponse { Message = decodeResult.Value.Message + "-grpc" };
                var encodeResult = codec.EncodeResponse(responsePayload, new ResponseMeta(encoding: "application/json"));
                if (encodeResult.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(encodeResult.Error!));
                }

                var response = Response<ReadOnlyMemory<byte>>.Create(encodeResult.Value, new ResponseMeta(encoding: "application/json"));
                return ValueTask.FromResult(Ok(response));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateUnaryClient<EchoRequest, EchoResponse>("echo", codec);
            var requestMeta = new RequestMeta(
                service: "echo",
                procedure: "ping",
                encoding: "application/json",
                transport: "grpc");
            var request = new Request<EchoRequest>(requestMeta, new EchoRequest("hello"));

            var result = await client.CallAsync(request, ct);

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal("hello-grpc", result.Value.Body.Message);
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task OnewayRoundtrip_OverGrpcTransport()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("audit");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "audit");
        options.AddOnewayOutbound("audit", null, grpcOutbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, object>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.Register(new OnewayProcedureSpec(
            "audit",
            "audit::record",
            (request, cancellationToken) =>
            {
                var decodeResult = codec.DecodeRequest(request.Body, request.Meta);
                if (decodeResult.IsFailure)
                {
                    return ValueTask.FromResult(Err<OnewayAck>(decodeResult.Error!));
                }

                received.TrySetResult(decodeResult.Value.Message);
                return ValueTask.FromResult(Ok(OnewayAck.Ack()));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateOnewayClient<EchoRequest>("audit", codec);
            var requestMeta = new RequestMeta(
                service: "audit",
                procedure: "audit::record",
                encoding: "application/json",
                transport: "grpc");
            var request = new Request<EchoRequest>(requestMeta, new EchoRequest("ping"));

            var ackResult = await client.CallAsync(request, ct);

            Assert.True(ackResult.IsSuccess, ackResult.Error?.Message);
            Assert.Equal("ping", await received.Task.WaitAsync(TimeSpan.FromSeconds(2), ct));
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task DuplexStreaming_OverGrpcTransport()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("chat");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "chat");
        options.AddDuplexOutbound("chat", null, grpcOutbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<ChatMessage, ChatMessage>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        await using var serverTasks = new ServerTaskTracker();

        dispatcher.Register(new DuplexProcedureSpec(
            "chat",
            "chat::talk",
            (request, cancellationToken) =>
            {
                var call = DuplexStreamCall.Create(request.Meta, new ResponseMeta(encoding: "application/json"));

                var pump = Task.Run(async () =>
                {
                    try
                    {
                        var handshake = codec.EncodeResponse(new ChatMessage("ready"), call.ResponseMeta);
                        if (handshake.IsFailure)
                        {
                            await call.CompleteResponsesAsync(handshake.Error!, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        await call.ResponseWriter.WriteAsync(handshake.Value, cancellationToken).ConfigureAwait(false);

                        await foreach (var payload in call.RequestReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                        {
                            var decode = codec.DecodeRequest(payload, request.Meta);
                            if (decode.IsFailure)
                            {
                                await call.CompleteResponsesAsync(decode.Error!, cancellationToken).ConfigureAwait(false);
                                return;
                            }

                            var response = new ChatMessage($"echo:{decode.Value.Message}");
                            var encode = codec.EncodeResponse(response, call.ResponseMeta);
                            if (encode.IsFailure)
                            {
                                await call.CompleteResponsesAsync(encode.Error!, cancellationToken).ConfigureAwait(false);
                                return;
                            }

                            await call.ResponseWriter.WriteAsync(encode.Value, cancellationToken).ConfigureAwait(false);
                        }

                        await call.CompleteResponsesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await call.CompleteResponsesAsync(PolymerErrorAdapter.FromStatus(
                            PolymerStatusCode.Internal,
                            ex.Message ?? "stream processing failure",
                            transport: TransportName), cancellationToken).ConfigureAwait(false);
                    }
                }, cancellationToken);

                serverTasks.Track(pump);

                return ValueTask.FromResult(Ok((IDuplexStreamCall)call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateDuplexStreamClient<ChatMessage, ChatMessage>("chat", codec);
            var requestMeta = new RequestMeta(
                service: "chat",
                procedure: "chat::talk",
                encoding: "application/json",
                transport: "grpc");

            await using var session = await client.StartAsync(requestMeta, ct);

            await session.WriteAsync(new ChatMessage("hello"), ct);
            await session.WriteAsync(new ChatMessage("world"), ct);
            await session.CompleteRequestsAsync(cancellationToken: ct);

            var responses = new List<string>();
            await foreach (var response in session.ReadResponsesAsync(ct))
            {
                responses.Add(response.Body.Message);
            }

            Assert.Equal(new[] { "ready", "echo:hello", "echo:world" }, responses);
            Assert.Equal("application/json", session.ResponseMeta.Encoding);
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task DuplexStreaming_ServerCancellationPropagatesToClient()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("chat");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "chat");
        options.AddDuplexOutbound("chat", null, grpcOutbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<ChatMessage, ChatMessage>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        await using var serverTasks = new ServerTaskTracker();

        dispatcher.Register(new DuplexProcedureSpec(
            "chat",
            "chat::cancel",
            (request, cancellationToken) =>
            {
                var call = DuplexStreamCall.Create(request.Meta, new ResponseMeta(encoding: "application/json"));

                var pump = Task.Run(async () =>
                {
                    var handshake = codec.EncodeResponse(new ChatMessage("ready"), call.ResponseMeta);
                    if (handshake.IsFailure)
                    {
                        await call.CompleteResponsesAsync(handshake.Error!, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await call.ResponseWriter.WriteAsync(handshake.Value, cancellationToken).ConfigureAwait(false);

                    await foreach (var payload in call.RequestReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var decode = codec.DecodeRequest(payload, request.Meta);
                        if (decode.IsFailure)
                        {
                            await call.CompleteResponsesAsync(decode.Error!, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        var response = new ChatMessage($"ack:{decode.Value.Message}");
                        var encode = codec.EncodeResponse(response, call.ResponseMeta);
                        if (encode.IsFailure)
                        {
                            await call.CompleteResponsesAsync(encode.Error!, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        await call.ResponseWriter.WriteAsync(encode.Value, cancellationToken).ConfigureAwait(false);

                        var error = PolymerErrorAdapter.FromStatus(
                            PolymerStatusCode.Cancelled,
                            "server cancelled",
                            transport: TransportName);
                        await call.CompleteResponsesAsync(error, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }, cancellationToken);

                serverTasks.Track(pump);

                return ValueTask.FromResult(Ok((IDuplexStreamCall)call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateDuplexStreamClient<ChatMessage, ChatMessage>("chat", codec);
            var requestMeta = new RequestMeta(
                service: "chat",
                procedure: "chat::cancel",
                encoding: "application/json",
                transport: "grpc");

            await using var session = await client.StartAsync(requestMeta, ct);
            await session.WriteAsync(new ChatMessage("first"), ct);
            await session.CompleteRequestsAsync(cancellationToken: ct);

            var enumerator = session.ReadResponsesAsync(ct).GetAsyncEnumerator(ct);
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("ready", enumerator.Current.Body.Message);

            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("ack:first", enumerator.Current.Body.Message);

            var exception = await Assert.ThrowsAsync<PolymerException>(async () =>
            {
                await enumerator.MoveNextAsync();
            });

            await enumerator.DisposeAsync();

            Assert.Equal(PolymerStatusCode.Cancelled, exception.StatusCode);
            Assert.Contains("server cancelled", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task DuplexStreaming_ClientCancellationPropagatesToServer()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("chat");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "chat");
        options.AddDuplexOutbound("chat", null, grpcOutbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<ChatMessage, ChatMessage>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var serverCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var serverTasks = new ServerTaskTracker();

        dispatcher.Register(new DuplexProcedureSpec(
            "chat",
            "chat::client-cancel",
            (request, cancellationToken) =>
            {
                var call = DuplexStreamCall.Create(request.Meta, new ResponseMeta(encoding: "application/json"));

                cancellationToken.Register(() => serverCancelled.TrySetResult(true));

                var pump = Task.Run(async () =>
                {
                    try
                    {
                        var handshake = codec.EncodeResponse(new ChatMessage("ready"), call.ResponseMeta);
                        if (handshake.IsFailure)
                        {
                            await call.CompleteResponsesAsync(handshake.Error!, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        await call.ResponseWriter.WriteAsync(handshake.Value, cancellationToken).ConfigureAwait(false);

                        await foreach (var payload in call.RequestReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                        {
                            var decode = codec.DecodeRequest(payload, request.Meta);
                            if (decode.IsFailure)
                            {
                                await call.CompleteResponsesAsync(decode.Error!, cancellationToken).ConfigureAwait(false);
                                return;
                            }

                            var response = new ChatMessage($"ack:{decode.Value.Message}");
                            var encode = codec.EncodeResponse(response, call.ResponseMeta);
                            if (encode.IsFailure)
                            {
                                await call.CompleteResponsesAsync(encode.Error!, cancellationToken).ConfigureAwait(false);
                                return;
                            }

                            await call.ResponseWriter.WriteAsync(encode.Value, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        serverCancelled.TrySetResult(true);
                    }
                    finally
                    {
                        await call.CompleteResponsesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }, cancellationToken);

                serverTasks.Track(pump);

                return ValueTask.FromResult(Ok((IDuplexStreamCall)call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateDuplexStreamClient<ChatMessage, ChatMessage>("chat", codec);
            var requestMeta = new RequestMeta(
                service: "chat",
                procedure: "chat::client-cancel",
                encoding: "application/json",
                transport: "grpc");

            using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            await using var session = await client.StartAsync(requestMeta, callCts.Token);
            await session.WriteAsync(new ChatMessage("hello"), ct);

            callCts.Cancel();

            var exception = await Assert.ThrowsAsync<PolymerException>(async () =>
            {
                await foreach (var _ in session.ReadResponsesAsync(ct))
                {
                }
            });

            Assert.Equal(PolymerStatusCode.Cancelled, exception.StatusCode);
            Assert.True(await serverCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), ct));
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task DuplexStreaming_FlowControl_ServerSlow()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("chat");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "chat");
        options.AddDuplexOutbound("chat", null, grpcOutbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<ChatMessage, ChatMessage>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        await using var serverTasks = new ServerTaskTracker();

        dispatcher.Register(new DuplexProcedureSpec(
            "chat",
            "chat::flow",
            (request, cancellationToken) =>
            {
                var call = DuplexStreamCall.Create(request.Meta, new ResponseMeta(encoding: "application/json"));

                var pump = Task.Run(async () =>
                {
                    var index = 0;
                    try
                    {
                        var handshake = codec.EncodeResponse(new ChatMessage("ready"), call.ResponseMeta);
                        if (handshake.IsFailure)
                        {
                            await call.CompleteResponsesAsync(handshake.Error!, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        await call.ResponseWriter.WriteAsync(handshake.Value, cancellationToken).ConfigureAwait(false);

                        await foreach (var payload in call.RequestReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                        {
                            index++;
                            await Task.Delay(15, cancellationToken).ConfigureAwait(false);

                            var decode = codec.DecodeRequest(payload, request.Meta);
                            if (decode.IsFailure)
                            {
                                await call.CompleteResponsesAsync(decode.Error!, cancellationToken).ConfigureAwait(false);
                                return;
                            }

                            var response = new ChatMessage($"ack-{index}:{decode.Value.Message}");
                            var encode = codec.EncodeResponse(response, call.ResponseMeta);
                            if (encode.IsFailure)
                            {
                                await call.CompleteResponsesAsync(encode.Error!, cancellationToken).ConfigureAwait(false);
                                return;
                            }

                            await call.ResponseWriter.WriteAsync(encode.Value, cancellationToken).ConfigureAwait(false);
                        }

                        await call.CompleteResponsesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await call.CompleteResponsesAsync(PolymerErrorAdapter.FromStatus(
                            PolymerStatusCode.Internal,
                            ex.Message ?? "flow-control failure",
                            transport: TransportName), cancellationToken).ConfigureAwait(false);
                    }
                }, cancellationToken);

                serverTasks.Track(pump);

                return ValueTask.FromResult(Ok((IDuplexStreamCall)call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateDuplexStreamClient<ChatMessage, ChatMessage>("chat", codec);
            var requestMeta = new RequestMeta(
                service: "chat",
                procedure: "chat::flow",
                encoding: "application/json",
                transport: "grpc");

            await using var session = await client.StartAsync(requestMeta, ct);

            const int messageCount = 10;
            for (var i = 0; i < messageCount; i++)
            {
                await session.WriteAsync(new ChatMessage($"msg-{i}"), ct);
            }

            await session.CompleteRequestsAsync(cancellationToken: ct);

            var responses = new List<string>();
            await foreach (var response in session.ReadResponsesAsync(ct))
            {
                responses.Add(response.Body.Message);
            }

            Assert.Equal(messageCount + 1, responses.Count);
            Assert.Equal("ready", responses[0]);
            for (var i = 0; i < messageCount; i++)
            {
                Assert.Equal($"ack-{i + 1}:msg-{i}", responses[i + 1]);
            }
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Unary_PropagatesMetadataBetweenClientAndServer()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("echo");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "echo");
        options.AddUnaryOutbound("echo", null, grpcOutbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var observedMeta = new TaskCompletionSource<RequestMeta>(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.Register(new UnaryProcedureSpec(
            "echo",
            "metadata",
            (request, cancellationToken) =>
            {
                observedMeta.TrySetResult(request.Meta);

                var decodeResult = codec.DecodeRequest(request.Body, request.Meta);
                if (decodeResult.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(decodeResult.Error!));
                }

                var responseMessage = new EchoResponse { Message = decodeResult.Value.Message };
                var responseMeta = new ResponseMeta(encoding: "application/json").WithHeader("x-response-id", "42");
                var encodeResult = codec.EncodeResponse(responseMessage, responseMeta);
                if (encodeResult.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(encodeResult.Error!));
                }

                var response = Response<ReadOnlyMemory<byte>>.Create(encodeResult.Value, responseMeta);
                return ValueTask.FromResult(Ok(response));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateUnaryClient<EchoRequest, EchoResponse>("echo", codec);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            var ttl = TimeSpan.FromSeconds(5);

            var requestMeta = new RequestMeta(
                service: "echo",
                procedure: "metadata",
                caller: "caller-123",
                encoding: "application/json",
                transport: "grpc",
                shardKey: "shard-1",
                routingKey: "route-1",
                routingDelegate: "delegate-1",
                timeToLive: ttl,
                deadline: deadline,
                headers: new[]
                {
                    new KeyValuePair<string, string>("x-trace-id", "trace-abc"),
                    new KeyValuePair<string, string>("x-feature", "beta")
                });

            var request = new Request<EchoRequest>(requestMeta, new EchoRequest("payload"));

            var result = await client.CallAsync(request, ct);
            Assert.True(result.IsSuccess, result.Error?.Message);

            var serverMeta = await observedMeta.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(requestMeta.Caller, serverMeta.Caller);
            Assert.Equal(requestMeta.ShardKey, serverMeta.ShardKey);
            Assert.Equal(requestMeta.RoutingKey, serverMeta.RoutingKey);
            Assert.Equal(requestMeta.RoutingDelegate, serverMeta.RoutingDelegate);
            Assert.Equal(requestMeta.TimeToLive, serverMeta.TimeToLive);

            Assert.True(serverMeta.Deadline.HasValue);
            Assert.InRange((serverMeta.Deadline.Value - deadline).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(5));

            Assert.Equal("trace-abc", serverMeta.Headers["x-trace-id"]);
            Assert.Equal("beta", serverMeta.Headers["x-feature"]);

            var responseMeta = result.Value.Meta;
            Assert.Equal("application/json", responseMeta.Encoding);
            Assert.True(responseMeta.TryGetHeader("x-response-id", out var responseId));
            Assert.Equal("42", responseId);
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ServerStreaming_PropagatesMetadataAndHeaders()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("stream");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound(address, "stream");
        options.AddStreamOutbound("stream", null, grpcOutbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var observedMeta = new TaskCompletionSource<RequestMeta>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var serverTasks = new ServerTaskTracker();

        dispatcher.Register(new StreamProcedureSpec(
            "stream",
            "stream::meta",
            (request, callOptions, cancellationToken) =>
            {
                observedMeta.TrySetResult(request.Meta);

                var streamCall = GrpcServerStreamCall.Create(
                    request.Meta,
                    new ResponseMeta(encoding: "application/json").WithHeader("x-stream-id", "stream-99"));

                var background = Task.Run(async () =>
                {
                    try
                    {
                        var encodeResult = codec.EncodeResponse(new EchoResponse { Message = "first" }, streamCall.ResponseMeta);
                        if (encodeResult.IsFailure)
                        {
                            await streamCall.CompleteAsync(encodeResult.Error!, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        await streamCall.WriteAsync(encodeResult.Value, cancellationToken).ConfigureAwait(false);

                        var secondEncode = codec.EncodeResponse(new EchoResponse { Message = "second" }, streamCall.ResponseMeta);
                        if (secondEncode.IsFailure)
                        {
                            await streamCall.CompleteAsync(secondEncode.Error!, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        await streamCall.WriteAsync(secondEncode.Value, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        await streamCall.CompleteAsync().ConfigureAwait(false);
                    }
                }, cancellationToken);

                serverTasks.Track(background);

                return ValueTask.FromResult(Ok<IStreamCall>(streamCall));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var client = dispatcher.CreateStreamClient<EchoRequest, EchoResponse>("stream", codec);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);

            var requestMeta = new RequestMeta(
                service: "stream",
                procedure: "stream::meta",
                caller: "stream-caller",
                encoding: "application/json",
                transport: "grpc",
                timeToLive: TimeSpan.FromSeconds(10),
                deadline: deadline,
                headers: new[]
                {
                    new KeyValuePair<string, string>("x-meta", "value")
                });

            var request = new Request<EchoRequest>(requestMeta, new EchoRequest("ignored"));

            var responses = new List<Response<EchoResponse>>();
            await foreach (var response in client.CallAsync(request, new StreamCallOptions(StreamDirection.Server), ct))
            {
                responses.Add(response);
            }

            Assert.Equal(2, responses.Count);
            Assert.Equal("first", responses[0].Body.Message);
            Assert.Equal("second", responses[1].Body.Message);

            var serverMeta = await observedMeta.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("stream-caller", serverMeta.Caller);
            Assert.Equal(TimeSpan.FromSeconds(10), serverMeta.TimeToLive);
            Assert.True(serverMeta.Deadline.HasValue);
            Assert.InRange((serverMeta.Deadline.Value - deadline).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(5));
            Assert.Equal("value", serverMeta.Headers["x-meta"]);

            foreach (var response in responses)
            {
                Assert.Equal("application/json", response.Meta.Encoding);
                Assert.True(response.Meta.TryGetHeader("x-stream-id", out var streamId));
                Assert.Equal("stream-99", streamId);
            }
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task GrpcTransport_ResponseTrailers_SurfaceEncodingAndStatus()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("meta");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        dispatcher.Register(new UnaryProcedureSpec(
            "meta",
            "success",
            (request, cancellationToken) =>
            {
                var decodeResult = codec.DecodeRequest(request.Body, request.Meta);
                if (decodeResult.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(decodeResult.Error!));
                }

                var responseMeta = new ResponseMeta(encoding: "application/json").WithHeader("x-meta-response", "yes");
                var encodeResult = codec.EncodeResponse(new EchoResponse { Message = decodeResult.Value.Message }, responseMeta);
                if (encodeResult.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(encodeResult.Error!));
                }

                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(encodeResult.Value, responseMeta)));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = new System.Net.Http.SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true
                }
            });

            var method = new Method<byte[], byte[]>(
                MethodType.Unary,
                "meta",
                "success",
                Marshallers.Create(static payload => payload ?? Array.Empty<byte>(), static payload => payload ?? Array.Empty<byte>()),
                Marshallers.Create(static payload => payload ?? Array.Empty<byte>(), static payload => payload ?? Array.Empty<byte>()));

            var metadata = new Metadata
            {
                { EncodingHeaderKey, "application/json" }
            };

            var payload = JsonSerializer.SerializeToUtf8Bytes(new EchoRequest("hello"));
            var call = channel.CreateCallInvoker().AsyncUnaryCall(method, null, new CallOptions(metadata, cancellationToken: ct), payload);

            var responseBytes = await call.ResponseAsync;
            var headers = await call.ResponseHeadersAsync;
            var trailers = call.GetTrailers();

            Assert.NotEmpty(responseBytes);

            bool encodingHeaderFound = headers.Any(entry => string.Equals(entry.Key, EncodingTrailerKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, "application/json", StringComparison.OrdinalIgnoreCase))
                || trailers.Any(entry => string.Equals(entry.Key, EncodingTrailerKey, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Value, "application/json", StringComparison.OrdinalIgnoreCase));
            Assert.True(encodingHeaderFound, "Expected polymer encoding metadata to be present in headers or trailers.");

            bool customHeaderFound = headers.Any(entry => string.Equals(entry.Key, "x-meta-response", StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, "yes", StringComparison.OrdinalIgnoreCase))
                || trailers.Any(entry => string.Equals(entry.Key, "x-meta-response", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Value, "yes", StringComparison.OrdinalIgnoreCase));
            Assert.True(customHeaderFound, "Expected custom response header to be present in headers or trailers.");

        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task GrpcTransport_ResponseTrailers_SurfaceErrorMetadata()
    {
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var options = new DispatcherOptions("meta");
        var grpcInbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);

        dispatcher.Register(new UnaryProcedureSpec(
            "meta",
            "fail",
            (request, cancellationToken) =>
            {
                var error = PolymerErrorAdapter.FromStatus(
                    PolymerStatusCode.PermissionDenied,
                    "access denied",
                    transport: TransportName);
                return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = new System.Net.Http.SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true
                }
            });

            var method = new Method<byte[], byte[]>(
                MethodType.Unary,
                "meta",
                "fail",
                Marshallers.Create(static payload => payload ?? Array.Empty<byte>(), static payload => payload ?? Array.Empty<byte>()),
                Marshallers.Create(static payload => payload ?? Array.Empty<byte>(), static payload => payload ?? Array.Empty<byte>()));

            var metadata = new Metadata();
            var payload = Array.Empty<byte>();

            var call = channel.CreateCallInvoker().AsyncUnaryCall(method, null, new CallOptions(metadata, cancellationToken: ct), payload);

            var rpcException = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseAsync);
            Assert.Equal(StatusCode.PermissionDenied, rpcException.StatusCode);

            var trailers = rpcException.Trailers;
            Assert.Contains(trailers, entry => string.Equals(entry.Key, ErrorMessageTrailerKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, "access denied", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(trailers, entry => string.Equals(entry.Key, ErrorCodeTrailerKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, "permission-denied", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(trailers, entry => string.Equals(entry.Key, StatusTrailerKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Value, PolymerStatusCode.PermissionDenied.ToString(), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    public static IEnumerable<object[]> FromStatusMappings()
    {
        yield return new object[] { StatusCode.Cancelled, PolymerStatusCode.Cancelled };
        yield return new object[] { StatusCode.InvalidArgument, PolymerStatusCode.InvalidArgument };
        yield return new object[] { StatusCode.DeadlineExceeded, PolymerStatusCode.DeadlineExceeded };
        yield return new object[] { StatusCode.NotFound, PolymerStatusCode.NotFound };
        yield return new object[] { StatusCode.AlreadyExists, PolymerStatusCode.AlreadyExists };
        yield return new object[] { StatusCode.PermissionDenied, PolymerStatusCode.PermissionDenied };
        yield return new object[] { StatusCode.ResourceExhausted, PolymerStatusCode.ResourceExhausted };
        yield return new object[] { StatusCode.FailedPrecondition, PolymerStatusCode.FailedPrecondition };
        yield return new object[] { StatusCode.Aborted, PolymerStatusCode.Aborted };
        yield return new object[] { StatusCode.OutOfRange, PolymerStatusCode.OutOfRange };
        yield return new object[] { StatusCode.Unimplemented, PolymerStatusCode.Unimplemented };
        yield return new object[] { StatusCode.Internal, PolymerStatusCode.Internal };
        yield return new object[] { StatusCode.Unavailable, PolymerStatusCode.Unavailable };
        yield return new object[] { StatusCode.DataLoss, PolymerStatusCode.DataLoss };
        yield return new object[] { StatusCode.Unknown, PolymerStatusCode.Unknown };
        yield return new object[] { StatusCode.Unauthenticated, PolymerStatusCode.PermissionDenied };
    }

    public static IEnumerable<object[]> ToStatusMappings()
    {
        yield return new object[] { StatusCode.Cancelled, PolymerStatusCode.Cancelled };
        yield return new object[] { StatusCode.InvalidArgument, PolymerStatusCode.InvalidArgument };
        yield return new object[] { StatusCode.DeadlineExceeded, PolymerStatusCode.DeadlineExceeded };
        yield return new object[] { StatusCode.NotFound, PolymerStatusCode.NotFound };
        yield return new object[] { StatusCode.AlreadyExists, PolymerStatusCode.AlreadyExists };
        yield return new object[] { StatusCode.PermissionDenied, PolymerStatusCode.PermissionDenied };
        yield return new object[] { StatusCode.ResourceExhausted, PolymerStatusCode.ResourceExhausted };
        yield return new object[] { StatusCode.FailedPrecondition, PolymerStatusCode.FailedPrecondition };
        yield return new object[] { StatusCode.Aborted, PolymerStatusCode.Aborted };
        yield return new object[] { StatusCode.OutOfRange, PolymerStatusCode.OutOfRange };
        yield return new object[] { StatusCode.Unimplemented, PolymerStatusCode.Unimplemented };
        yield return new object[] { StatusCode.Internal, PolymerStatusCode.Internal };
        yield return new object[] { StatusCode.Unavailable, PolymerStatusCode.Unavailable };
        yield return new object[] { StatusCode.DataLoss, PolymerStatusCode.DataLoss };
        yield return new object[] { StatusCode.Unknown, PolymerStatusCode.Unknown };
    }

    [Theory]
    [MemberData(nameof(FromStatusMappings))]
    public void GrpcStatusMapper_FromStatus_MapsExpected(StatusCode statusCode, PolymerStatusCode expected)
    {
        var mapperType = typeof(GrpcOutbound).Assembly.GetType("Polymer.Transport.Grpc.GrpcStatusMapper", throwOnError: true)
            ?? throw new InvalidOperationException("Unable to locate GrpcStatusMapper type.");
        var method = mapperType.GetMethod("FromStatus", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate FromStatus method.");

        var status = new Status(statusCode, "detail");
        var result = (PolymerStatusCode)method.Invoke(null, new object[] { status })!;

        Assert.Equal(expected, result);
    }

    [Theory]
    [MemberData(nameof(ToStatusMappings))]
    public void GrpcStatusMapper_ToStatus_MapsExpected(StatusCode expectedStatusCode, PolymerStatusCode polymerStatus)
    {
        var mapperType = typeof(GrpcOutbound).Assembly.GetType("Polymer.Transport.Grpc.GrpcStatusMapper", throwOnError: true)
            ?? throw new InvalidOperationException("Unable to locate GrpcStatusMapper type.");
        var method = mapperType.GetMethod("ToStatus", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Unable to locate ToStatus method.");

        var status = (Status)method.Invoke(null, new object[] { polymerStatus, "detail" })!;

        Assert.Equal(expectedStatusCode, status.StatusCode);
    }

    private static async Task WaitForGrpcReadyAsync(Uri address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        const int maxAttempts = 100;
        const int connectTimeoutMilliseconds = 200;
        const int settleDelayMilliseconds = 50;
        const int retryDelayMilliseconds = 20;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address.Host, address.Port)
                            .WaitAsync(TimeSpan.FromMilliseconds(connectTimeoutMilliseconds), cancellationToken)
                            .ConfigureAwait(false);

                await Task.Delay(TimeSpan.FromMilliseconds(settleDelayMilliseconds), cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SocketException)
            {
                // Listener not ready yet; retry.
            }
            catch (TimeoutException)
            {
                // Connection attempt timed out; retry.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(retryDelayMilliseconds), cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("The gRPC inbound failed to bind within the allotted time.");
    }

    private sealed class ServerTaskTracker : IAsyncDisposable
    {
        private readonly List<Task> _tasks = new();

        public void Track(Task task)
        {
            if (task is null)
            {
                return;
            }

            lock (_tasks)
            {
                _tasks.Add(task);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task[] toAwait;
            lock (_tasks)
            {
                if (_tasks.Count == 0)
                {
                    return;
                }

                toAwait = _tasks.ToArray();
                _tasks.Clear();
            }

            try
            {
                await Task.WhenAll(toAwait).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation tokens propagate during shutdown.
            }
        }
    }

    private sealed record EchoRequest(string Message);

    private sealed record EchoResponse
    {
        public string Message { get; init; } = string.Empty;
    }

    private sealed record AggregateChunk(int Amount);

    private sealed record AggregateResponse(int TotalAmount);

    private sealed record ChatMessage(string Message);
}
