using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Transport.Http;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Transport;

public class HttpTransportTests
{
    [Fact(Timeout = 30000)]
    public async Task UnaryRoundtrip_EncodesAndDecodesPayload()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("echo");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http-inbound", httpInbound);

        var httpClient = new HttpClient { BaseAddress = baseAddress };
        var httpOutbound = new HttpOutbound(httpClient, baseAddress, disposeClient: true);
        options.AddUnaryOutbound("echo", null, httpOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        dispatcher.Register(new UnaryProcedureSpec(
            "echo",
            "ping",
            async (request, cancellationToken) =>
            {
                var decodeResult = codec.DecodeRequest(request.Body, request.Meta);
                if (decodeResult.IsFailure)
                {
                    return Err<Response<ReadOnlyMemory<byte>>>(decodeResult.Error!);
                }

                var responsePayload = new EchoResponse { Message = decodeResult.Value.Message.ToUpperInvariant() };
                var encodeResult = codec.EncodeResponse(responsePayload, new ResponseMeta(encoding: "application/json"));
                if (encodeResult.IsFailure)
                {
                    return Err<Response<ReadOnlyMemory<byte>>>(encodeResult.Error!);
                }

                var response = Response<ReadOnlyMemory<byte>>.Create(encodeResult.Value, new ResponseMeta(encoding: "application/json"));
                return Ok(response);
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        var client = dispatcher.CreateUnaryClient<EchoRequest, EchoResponse>("echo", codec);

        var requestMeta = new RequestMeta(
            service: "echo",
            procedure: "ping",
            encoding: "application/json",
            transport: "http");

        var request = new Request<EchoRequest>(requestMeta, new EchoRequest("hello"));

        var result = await client.CallAsync(request, ct);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("HELLO", result.Value.Body.Message);

        await dispatcher.StopOrThrowAsync(ct);
    }

    private sealed record EchoRequest(string Message)
    {
        public string Message { get; init; } = Message;
    }

    private sealed record EchoResponse
    {
        public string Message { get; init; } = string.Empty;
    }

    private sealed record ChatMessage(string Message)
    {
        public string Message { get; init; } = Message;
    }

    [Fact(Timeout = 30000)]
    public async Task OnewayRoundtrip_SucceedsWithAck()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("echo");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http-inbound", httpInbound);

        var httpClient = new HttpClient { BaseAddress = baseAddress };
        var httpOutbound = new HttpOutbound(httpClient, baseAddress, disposeClient: true);
        options.AddOnewayOutbound("echo", null, httpOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var codec = new JsonCodec<EchoRequest, object>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        dispatcher.Register(new OnewayProcedureSpec(
            "echo",
            "notify",
            (request, cancellationToken) =>
            {
                var decodeResult = codec.DecodeRequest(request.Body, request.Meta);
                if (decodeResult.IsFailure)
                {
                    return ValueTask.FromResult(Err<OnewayAck>(decodeResult.Error!));
                }

                received.TrySetResult(decodeResult.Value.Message);
                return ValueTask.FromResult(Ok(OnewayAck.Ack(new ResponseMeta(encoding: "application/json"))));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        var client = dispatcher.CreateOnewayClient<EchoRequest>("echo", codec);
        var requestMeta = new RequestMeta(
            service: "echo",
            procedure: "notify",
            encoding: "application/json",
            transport: "http");
        var request = new Request<EchoRequest>(requestMeta, new EchoRequest("ping"));

        var ackResult = await client.CallAsync(request, ct);

        Assert.True(ackResult.IsSuccess, ackResult.Error?.Message);
        Assert.Equal("ping", await received.Task.WaitAsync(TimeSpan.FromSeconds(2), ct));

        await dispatcher.StopOrThrowAsync(ct);
    }

    [Fact(Timeout = 30000)]
    public async Task ServerStreaming_EmitsEventStream()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("stream");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http-inbound", httpInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.Register(new StreamProcedureSpec(
            "stream",
            "stream::events",
            (request, callOptions, cancellationToken) =>
            {
                var streamCall = HttpStreamCall.CreateServerStream(
                    request.Meta,
                    new ResponseMeta(encoding: "text/plain"));

                _ = Task.Run(async () =>
                {
                    try
                    {
                        for (var index = 0; index < 3; index++)
                        {
                            var payload = Encoding.UTF8.GetBytes($"event-{index}");
                            await streamCall.WriteAsync(payload, cancellationToken);
                            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
                        }
                    }
                    finally
                    {
                        await streamCall.CompleteAsync();
                    }
                }, cancellationToken);

                return ValueTask.FromResult(Ok<IStreamCall>(streamCall));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        var httpClient = new HttpClient { BaseAddress = baseAddress };
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "stream::events");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        using var response = await httpClient.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, ct);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.TryGetValues("X-Accel-Buffering", out var xab) || response.Content.Headers.TryGetValues("X-Accel-Buffering", out xab));
        Assert.Contains("no", xab);
        using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(responseStream, Encoding.UTF8);

        var events = new List<string>();

        while (events.Count < 3)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                events.Add(line.Substring("data:".Length).Trim());
            }
        }

        Assert.Equal(new[] { "event-0", "event-1", "event-2" }, events);

        await dispatcher.StopOrThrowAsync(ct);
    }

    [Fact(Timeout = 30000)]
    public async Task ServerStreaming_BinaryPayloadsAreBase64Encoded()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("stream-b64");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http-inbound", httpInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.Register(new StreamProcedureSpec(
            "stream-b64",
            "stream::binary",
            (request, callOptions, cancellationToken) =>
            {
                _ = callOptions;
                var streamCall = HttpStreamCall.CreateServerStream(
                    request.Meta,
                    new ResponseMeta(encoding: "application/octet-stream"));

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var payload = new byte[] { 0x00, 0x01, 0x02 };
                        await streamCall.WriteAsync(payload, cancellationToken);
                    }
                    finally
                    {
                        await streamCall.CompleteAsync();
                    }
                }, cancellationToken);

                return ValueTask.FromResult(Ok<IStreamCall>(streamCall));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        var httpClient = new HttpClient { BaseAddress = baseAddress };
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "stream::binary");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        using var response = await httpClient.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.True(response.IsSuccessStatusCode);

        using var responseStream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(responseStream, Encoding.UTF8);

        var dataLine = await reader.ReadLineAsync(ct);
        var encodingLine = await reader.ReadLineAsync(ct);
        var blankLine = await reader.ReadLineAsync(ct);

        Assert.Equal("data: AAEC", dataLine);
        Assert.Equal("encoding: base64", encodingLine);
        Assert.Equal(string.Empty, blankLine);

        await dispatcher.StopOrThrowAsync(ct);
    }

    [Fact(Timeout = 30000)]
    public async Task ServerStreaming_PayloadAboveLimit_FaultsStream()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("stream-limit");
        var runtime = new HttpServerRuntimeOptions { ServerStreamMaxMessageBytes = 8 };
        HttpStreamCall? streamCall = null;
        var httpInbound = new HttpInbound([baseAddress.ToString()], serverRuntimeOptions: runtime);
        options.AddLifecycle("http-inbound", httpInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.Register(new StreamProcedureSpec(
            "stream-limit",
            "stream::oversized",
            (request, callOptions, cancellationToken) =>
            {
                _ = callOptions;
                var call = HttpStreamCall.CreateServerStream(
                    request.Meta,
                    new ResponseMeta(encoding: "text/plain"));
                streamCall = call;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var payload = "this-payload-is-way-too-long"u8.ToArray();
                        await call.WriteAsync(payload, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // ignore cancellation when the response aborts
                    }
                }, cancellationToken);

                return ValueTask.FromResult(Ok<IStreamCall>(call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        var httpClient = new HttpClient { BaseAddress = baseAddress };
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "stream::oversized");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

        using var response = await httpClient.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.True(response.IsSuccessStatusCode);

        await WaitForCompletionAsync(ct);

        Assert.NotNull(streamCall);
        Assert.Equal(StreamCompletionStatus.Faulted, streamCall!.Context.CompletionStatus);
        var completionError = streamCall.Context.CompletionError;
        Assert.NotNull(completionError);
        Assert.Equal(OmniRelayStatusCode.ResourceExhausted, OmniRelayErrorAdapter.ToStatus(completionError!));

        await dispatcher.StopOrThrowAsync(ct);

        async Task WaitForCompletionAsync(CancellationToken cancellationToken)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (streamCall is not null && streamCall.Context.CompletionStatus != StreamCompletionStatus.None)
                {
                    return;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException("Server stream did not complete.");
                }

                await Task.Delay(50, cancellationToken);
            }
        }
    }

    [Fact(Timeout = 30000)]
    public async Task DuplexStreaming_OverHttpWebSocket()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("chat");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http-inbound", httpInbound);

        var httpDuplexOutbound = new HttpDuplexOutbound(baseAddress);
        options.AddDuplexOutbound("chat", null, httpDuplexOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<ChatMessage, ChatMessage>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, encoding: "application/json");

        dispatcher.Register(new DuplexProcedureSpec(
            "chat",
            "chat::echo",
            (request, cancellationToken) =>
            {
                var call = DuplexStreamCall.Create(request.Meta, new ResponseMeta(encoding: "application/json"));
                call.SetResponseMeta(new ResponseMeta(encoding: "application/json", transport: "http"));

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var payload in call.RequestReader.ReadAllAsync(cancellationToken))
                        {
                            var decode = codec.DecodeRequest(payload, request.Meta);
                            if (decode.IsFailure)
                            {
                                await call.CompleteResponsesAsync(decode.Error!, cancellationToken);
                                return;
                            }

                            var message = decode.Value;
                            var responsePayload = codec.EncodeResponse(message, call.ResponseMeta);
                            if (responsePayload.IsFailure)
                            {
                                await call.CompleteResponsesAsync(responsePayload.Error!, cancellationToken);
                                return;
                            }

                            await call.ResponseWriter.WriteAsync(responsePayload.Value, cancellationToken);
                        }

                        await call.CompleteResponsesAsync(cancellationToken: cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        await call.CompleteResponsesAsync(OmniRelayErrorAdapter.FromStatus(
                            OmniRelayStatusCode.Cancelled,
                            "cancelled",
                            transport: "http"), CancellationToken.None);
                    }
                }, cancellationToken);

                return ValueTask.FromResult(Ok((IDuplexStreamCall)call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        var client = dispatcher.CreateDuplexStreamClient<ChatMessage, ChatMessage>("chat", codec);
        var requestMeta = new RequestMeta(
            service: "chat",
            procedure: "chat::echo",
            encoding: "application/json",
            transport: "http");

        var sessionResult = await client.StartAsync(requestMeta, ct);
        await using var session = sessionResult.ValueOrThrow();

        (await session.WriteAsync(new ChatMessage("hello"), ct)).ThrowIfFailure();
        (await session.WriteAsync(new ChatMessage("world"), ct)).ThrowIfFailure();
        await session.CompleteRequestsAsync(cancellationToken: ct);

        var messages = new List<string>();
        await foreach (var response in session.ReadResponsesAsync(ct))
        {
            messages.Add(response.ValueOrThrow().Body.Message);
        }

        Assert.Equal(new[] { "hello", "world" }, messages);

        await dispatcher.StopOrThrowAsync(ct);
    }

    [Fact(Timeout = 30000)]
    public async Task DuplexStreaming_ServerCancels_PropagatesToClient()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("chat");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http-inbound", httpInbound);

        var httpDuplexOutbound = new HttpDuplexOutbound(baseAddress);
        options.AddDuplexOutbound("chat", null, httpDuplexOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        var codec = new JsonCodec<ChatMessage, ChatMessage>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, encoding: "application/json");

        dispatcher.Register(new DuplexProcedureSpec(
            "chat",
            "chat::echo",
            (request, cancellationToken) =>
            {
                var call = DuplexStreamCall.Create(request.Meta, new ResponseMeta(encoding: "application/json"));

                _ = Task.Run(async () =>
                {
                    // Avoid coupling to request cancellation to make test deterministic
                    Thread.Sleep(TimeSpan.FromMilliseconds(20));
                    await call.CompleteResponsesAsync(OmniRelayErrorAdapter.FromStatus(
                        OmniRelayStatusCode.Cancelled,
                        "cancelled",
                        transport: "http"), CancellationToken.None);
                }, CancellationToken.None);

                return ValueTask.FromResult(Ok((IDuplexStreamCall)call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        var client = dispatcher.CreateDuplexStreamClient<ChatMessage, ChatMessage>("chat", codec);
        var requestMeta = new RequestMeta(
            service: "chat",
            procedure: "chat::echo",
            transport: "http");

        var sessionResult = await client.StartAsync(requestMeta, ct);
        await using var session = sessionResult.ValueOrThrow();

        await using var enumerator = session.ReadResponsesAsync(ct).GetAsyncEnumerator(ct);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(enumerator.Current.IsFailure);
        Assert.Equal(OmniRelayStatusCode.Cancelled, OmniRelayErrorAdapter.ToStatus(enumerator.Current.Error!));

        await dispatcher.StopOrThrowAsync(ct);
    }
}
