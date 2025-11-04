namespace YARPCore.Tests.Transport;

// public class HttpTransportTests
// {
//     [Fact]
//     public async Task UnaryRoundtrip_EncodesAndDecodesPayload()
//     {
//         var port = TestPortAllocator.GetRandomPort();
//         var baseAddress = new Uri($"http://127.0.0.1:{port}/");

//         var options = new DispatcherOptions("echo");
//         var httpInbound = new HttpInbound([baseAddress.ToString()]);
//         options.AddLifecycle("http-inbound", httpInbound);

//         var httpClient = new HttpClient { BaseAddress = baseAddress };
//         var httpOutbound = new HttpOutbound(httpClient, baseAddress, disposeClient: true);
//         options.AddUnaryOutbound("echo", null, httpOutbound);

//         var dispatcher = new Polymer.Dispatcher.Dispatcher(options);

//         var codec = new JsonCodec<EchoRequest, EchoResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

//         dispatcher.Register(new UnaryProcedureSpec(
//             "echo",
//             "ping",
//             async (request, cancellationToken) =>
//             {
//                 var decodeResult = codec.DecodeRequest(request.Body, request.Meta);
//                 if (decodeResult.IsFailure)
//                 {
//                     return Err<Response<ReadOnlyMemory<byte>>>(decodeResult.Error!);
//                 }

//                 var responsePayload = new EchoResponse { Message = decodeResult.Value.Message.ToUpperInvariant() };
//                 var encodeResult = codec.EncodeResponse(responsePayload, new ResponseMeta(encoding: "application/json"));
//                 if (encodeResult.IsFailure)
//                 {
//                     return Err<Response<ReadOnlyMemory<byte>>>(encodeResult.Error!);
//                 }

//                 var response = Response<ReadOnlyMemory<byte>>.Create(encodeResult.Value, new ResponseMeta(encoding: "application/json"));
//                 return Ok(response);
//             }));

//         var ct = TestContext.Current.CancellationToken;
//         await dispatcher.StartAsync(ct);

//         var client = dispatcher.CreateUnaryClient<EchoRequest, EchoResponse>("echo", codec);

//         var requestMeta = new RequestMeta(
//             service: "echo",
//             procedure: "ping",
//             encoding: "application/json",
//             transport: "http");

//         var request = new Request<EchoRequest>(requestMeta, new EchoRequest("hello"));

//         var result = await client.CallAsync(request, ct);

//         Assert.True(result.IsSuccess, result.Error?.Message);
//         Assert.Equal("HELLO", result.Value.Body.Message);

//         await dispatcher.StopAsync(ct);
//     }

//     private sealed record EchoRequest(string Message);

//     private sealed record EchoResponse
//     {
//         public string Message { get; init; } = string.Empty;
//     }

//     private sealed record ChatMessage(string Message);

//     [Fact]
//     public async Task OnewayRoundtrip_SucceedsWithAck()
//     {
//         var port = TestPortAllocator.GetRandomPort();
//         var baseAddress = new Uri($"http://127.0.0.1:{port}/");

//         var options = new DispatcherOptions("echo");
//         var httpInbound = new HttpInbound([baseAddress.ToString()]);
//         options.AddLifecycle("http-inbound", httpInbound);

//         var httpClient = new HttpClient { BaseAddress = baseAddress };
//         var httpOutbound = new HttpOutbound(httpClient, baseAddress, disposeClient: true);
//         options.AddOnewayOutbound("echo", null, httpOutbound);

//         var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
//         var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
//         var codec = new JsonCodec<EchoRequest, object>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

//         dispatcher.Register(new OnewayProcedureSpec(
//             "echo",
//             "notify",
//             (request, cancellationToken) =>
//             {
//                 var decodeResult = codec.DecodeRequest(request.Body, request.Meta);
//                 if (decodeResult.IsFailure)
//                 {
//                     return ValueTask.FromResult(Err<OnewayAck>(decodeResult.Error!));
//                 }

//                 received.TrySetResult(decodeResult.Value.Message);
//                 return ValueTask.FromResult(Ok(OnewayAck.Ack(new ResponseMeta(encoding: "application/json"))));
//             }));

//         var ct = TestContext.Current.CancellationToken;
//         await dispatcher.StartAsync(ct);

//         var client = dispatcher.CreateOnewayClient<EchoRequest>("echo", codec);
//         var requestMeta = new RequestMeta(
//             service: "echo",
//             procedure: "notify",
//             encoding: "application/json",
//             transport: "http");
//         var request = new Request<EchoRequest>(requestMeta, new EchoRequest("ping"));

//         var ackResult = await client.CallAsync(request, ct);

//         Assert.True(ackResult.IsSuccess, ackResult.Error?.Message);
//         Assert.Equal("ping", await received.Task.WaitAsync(TimeSpan.FromSeconds(2), ct));

//         await dispatcher.StopAsync(ct);
//     }

//     [Fact]
//     public async Task ServerStreaming_EmitsEventStream()
//     {
//         var port = TestPortAllocator.GetRandomPort();
//         var baseAddress = new Uri($"http://127.0.0.1:{port}/");

//         var options = new DispatcherOptions("stream");
//         var httpInbound = new HttpInbound([baseAddress.ToString()]);
//         options.AddLifecycle("http-inbound", httpInbound);

//         var dispatcher = new Polymer.Dispatcher.Dispatcher(options);

//         dispatcher.Register(new StreamProcedureSpec(
//             "stream",
//             "stream::events",
//             (request, callOptions, cancellationToken) =>
//             {
//                 var streamCall = HttpStreamCall.CreateServerStream(
//                     request.Meta,
//                     new ResponseMeta(encoding: "text/plain"));

//                 _ = Task.Run(async () =>
//                 {
//                     try
//                     {
//                         for (var index = 0; index < 3; index++)
//                         {
//                             var payload = Encoding.UTF8.GetBytes($"event-{index}");
//                             await streamCall.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
//                             await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
//                         }
//                     }
//                     finally
//                     {
//                         await streamCall.CompleteAsync().ConfigureAwait(false);
//                     }
//                 }, cancellationToken);

//                 return ValueTask.FromResult(Ok<IStreamCall>(streamCall));
//             }));

//         var ct = TestContext.Current.CancellationToken;
//         await dispatcher.StartAsync(ct);

//         var httpClient = new HttpClient { BaseAddress = baseAddress };
//         var request = new HttpRequestMessage(HttpMethod.Get, "/");
//         request.Headers.Add(HttpTransportHeaders.Procedure, "stream::events");
//         request.Headers.Accept.ParseAdd("text/event-stream");

//         using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

//         Assert.True(response.IsSuccessStatusCode);
//         using var responseStream = await response.Content.ReadAsStreamAsync(ct);
//         using var reader = new StreamReader(responseStream, Encoding.UTF8);

//         var events = new System.Collections.Generic.List<string>();

//         while (events.Count < 3)
//         {
//             var line = await reader.ReadLineAsync(ct);
//             if (line is null)
//             {
//                 break;
//             }

//             if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
//             {
//                 events.Add(line.Substring("data:".Length).Trim());
//             }
//         }

//         Assert.Equal(new[] { "event-0", "event-1", "event-2" }, events);

//         await dispatcher.StopAsync(ct);
//     }

//     [Fact]
//     public async Task DuplexStreaming_OverHttpWebSocket()
//     {
//         var port = TestPortAllocator.GetRandomPort();
//         var baseAddress = new Uri($"http://127.0.0.1:{port}/");

//         var options = new DispatcherOptions("chat");
//         var httpInbound = new HttpInbound([baseAddress.ToString()]);
//         options.AddLifecycle("http-inbound", httpInbound);

//         var httpDuplexOutbound = new HttpDuplexOutbound(baseAddress);
//         options.AddDuplexOutbound("chat", null, httpDuplexOutbound);

//         var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
//         var codec = new JsonCodec<ChatMessage, ChatMessage>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, encoding: "application/json");

//         dispatcher.Register(new DuplexProcedureSpec(
//             "chat",
//             "chat::echo",
//             (request, cancellationToken) =>
//             {
//                 var call = DuplexStreamCall.Create(request.Meta, new ResponseMeta(encoding: "application/json"));
//                 call.SetResponseMeta(new ResponseMeta(encoding: "application/json", transport: "http"));

//                 _ = Task.Run(async () =>
//                 {
//                     try
//                     {
//                         await foreach (var payload in call.RequestReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
//                         {
//                             var decode = codec.DecodeRequest(payload, request.Meta);
//                             if (decode.IsFailure)
//                             {
//                                 await call.CompleteResponsesAsync(decode.Error!, cancellationToken).ConfigureAwait(false);
//                                 return;
//                             }

//                             var message = decode.Value;
//                             var responsePayload = codec.EncodeResponse(message, call.ResponseMeta);
//                             if (responsePayload.IsFailure)
//                             {
//                                 await call.CompleteResponsesAsync(responsePayload.Error!, cancellationToken).ConfigureAwait(false);
//                                 return;
//                             }

//                             await call.ResponseWriter.WriteAsync(responsePayload.Value, cancellationToken).ConfigureAwait(false);
//                         }

//                         await call.CompleteResponsesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
//                     }
//                     catch (OperationCanceledException)
//                     {
//                         await call.CompleteResponsesAsync(PolymerErrorAdapter.FromStatus(
//                             PolymerStatusCode.Cancelled,
//                             "cancelled",
//                             transport: "http"), CancellationToken.None).ConfigureAwait(false);
//                     }
//                 }, cancellationToken);

//                 return ValueTask.FromResult(Ok((IDuplexStreamCall)call));
//             }));

//         var ct = TestContext.Current.CancellationToken;
//         await dispatcher.StartAsync(ct);

//         var client = dispatcher.CreateDuplexStreamClient<ChatMessage, ChatMessage>("chat", codec);
//         var requestMeta = new RequestMeta(
//             service: "chat",
//             procedure: "chat::echo",
//             encoding: "application/json",
//             transport: "http");

//         await using var session = await client.StartAsync(requestMeta, ct);

//         await session.WriteAsync(new ChatMessage("hello"), ct);
//         await session.WriteAsync(new ChatMessage("world"), ct);
//         await session.CompleteRequestsAsync(cancellationToken: ct);

//         var messages = new System.Collections.Generic.List<string>();
//         await foreach (var response in session.ReadResponsesAsync(ct))
//         {
//             messages.Add(response.Body.Message);
//         }

//         Assert.Equal(new[] { "hello", "world" }, messages);

//         await dispatcher.StopAsync(ct);
//     }

//     [Fact]
//     public async Task DuplexStreaming_ServerCancels_PropagatesToClient()
//     {
//         var port = TestPortAllocator.GetRandomPort();
//         var baseAddress = new Uri($"http://127.0.0.1:{port}/");

//         var options = new DispatcherOptions("chat");
//         var httpInbound = new HttpInbound([baseAddress.ToString()]);
//         options.AddLifecycle("http-inbound", httpInbound);

//         var httpDuplexOutbound = new HttpDuplexOutbound(baseAddress);
//         options.AddDuplexOutbound("chat", null, httpDuplexOutbound);

//         var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
//         var codec = new JsonCodec<ChatMessage, ChatMessage>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, encoding: "application/json");

//         dispatcher.Register(new DuplexProcedureSpec(
//             "chat",
//             "chat::echo",
//             (request, cancellationToken) =>
//             {
//                 var call = DuplexStreamCall.Create(request.Meta, new ResponseMeta(encoding: "application/json"));

//                 _ = Task.Run(async () =>
//                 {
//                     await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
//                     await call.CompleteResponsesAsync(PolymerErrorAdapter.FromStatus(
//                         PolymerStatusCode.Cancelled,
//                         "cancelled",
//                         transport: "http"), cancellationToken).ConfigureAwait(false);
//                 }, cancellationToken);

//                 return ValueTask.FromResult(Ok((IDuplexStreamCall)call));
//             }));

//         var ct = TestContext.Current.CancellationToken;
//         await dispatcher.StartAsync(ct);

//         var client = dispatcher.CreateDuplexStreamClient<ChatMessage, ChatMessage>("chat", codec);
//         var requestMeta = new RequestMeta(
//             service: "chat",
//             procedure: "chat::echo",
//             transport: "http");

//         await using var session = await client.StartAsync(requestMeta, ct);

//         var ex = await Assert.ThrowsAsync<PolymerException>(async () =>
//         {
//             await foreach (var response in session.ReadResponsesAsync(ct))
//             {
//                 _ = response;
//             }
//         });

//         Assert.Equal(PolymerStatusCode.Cancelled, ex.StatusCode);

//         await dispatcher.StopAsync(ct);
//     }
// }
