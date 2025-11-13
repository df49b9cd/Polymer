using System.Collections.Immutable;
using System.Diagnostics;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Peers;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Samples.Quickstart;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;
using static Hugo.Go;

var runtime = SampleBootstrap.Build();

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

await runtime.Dispatcher.StartOrThrowAsync().ConfigureAwait(false);
Console.WriteLine("OmniRelay quickstart dispatcher is running.");
Console.WriteLine($" HTTP unary + oneway:   {string.Join(", ", runtime.HttpInbound.Urls)}");
Console.WriteLine($" gRPC unary + streaming: {string.Join(", ", runtime.GrpcInbound.Urls)}");
Console.WriteLine();
Console.WriteLine("Send Ctrl+C to stop.");

try
{
    await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    // Expected when Ctrl+C is pressed.
}

Console.WriteLine("Stopping dispatcher...");
await runtime.Dispatcher.StopOrThrowAsync().ConfigureAwait(false);
Console.WriteLine("Dispatcher stopped.");

namespace OmniRelay.Samples.Quickstart
{
    internal static class SampleBootstrap
    {
        public static SampleRuntime Build()
        {
            const string serviceName = "samples.quickstart";

            var httpInbound = new HttpInbound(["http://127.0.0.1:8080"]);
            var grpcInbound = new GrpcInbound(["http://127.0.0.1:9090"]);

            var options = new DispatcherOptions(serviceName);
            options.AddLifecycle("http-inbound", httpInbound);
            options.AddLifecycle("grpc-inbound", grpcInbound);

            // Outbound gRPC client demonstrating a custom peer chooser. Replace with real downstream addresses.
            var inventoryOutbound = new GrpcOutbound(
                [new Uri("http://127.0.0.1:10000")],
                remoteService: "inventory",
                peerChooser: peers => new FewestPendingPeerChooser(ImmutableArray.CreateRange(peers)));

            options.AddUnaryOutbound("inventory", null, inventoryOutbound);
            options.AddStreamOutbound("inventory", null, inventoryOutbound);

            // HTTP oneway outbound placeholder for audit fan-out.
            var auditClient = new HttpClient
            {
                BaseAddress = new Uri("http://127.0.0.1:11000")
            };
            var auditOutbound = new HttpOutbound(
                auditClient,
                new Uri("http://127.0.0.1:11000/yarpc/v1/audit::record"),
                disposeClient: true);
            options.AddOnewayOutbound("audit", null, auditOutbound);

            var logging = new ConsoleLoggingMiddleware();
            options.UnaryInboundMiddleware.Add(logging);
            options.OnewayInboundMiddleware.Add(logging);
            options.StreamInboundMiddleware.Add(logging);

            var dispatcher = new Dispatcher.Dispatcher(options);
            SampleProcedures.Register(dispatcher);
            return new SampleRuntime(dispatcher, httpInbound, grpcInbound);
        }
    }

    internal static class SampleProcedures
    {
        public static void Register(Dispatcher.Dispatcher dispatcher)
        {
            var greetCodec = new JsonCodec<GreetRequest, GreetResponse>();
            dispatcher.RegisterJsonUnary<GreetRequest, GreetResponse>(
                "hello::greet", (context, request) =>
                {
                    var greeting = $"Hello {request.Name}!";
                    var transport = context.RequestMeta.Transport ?? "unknown";
                    var response = new GreetResponse(greeting, transport, DateTimeOffset.UtcNow);
                    return ValueTask.FromResult(Response<GreetResponse>.Create(response));
                },
                configureProcedure: builder => builder.AddAliases(["hello::wave"]));

            var publishCodec = new JsonCodec<TelemetryEvent, object>();
            dispatcher.RegisterOneway(
                "telemetry::publish",
                builder =>
                {
                    builder.WithEncoding(publishCodec.Encoding);
                    builder.Handle((request, _) =>
                    {
                        var decode = publishCodec.DecodeRequest(request.Body, request.Meta);
                        if (decode.IsFailure)
                        {
                            return ValueTask.FromResult(Err<OnewayAck>(decode.Error!));
                        }

                        var evt = decode.Value;
                        Console.WriteLine($"[telemetry] {evt.Level.ToUpperInvariant()} {evt.Area}: {evt.Message}");

                        return ValueTask.FromResult<Result<OnewayAck>>(Ok(OnewayAck.Ack(new ResponseMeta(encoding: publishCodec.Encoding))));
                    });
                });

            var streamCodec = new JsonCodec<WeatherStreamRequest, WeatherUpdate>();
            dispatcher.RegisterStream(
                "weather::stream",
                builder =>
                {
                    builder.WithEncoding(streamCodec.Encoding);
                    builder.Handle((request, _, cancellationToken) =>
                        StreamingHandlers.HandleWeatherStreamAsync(streamCodec, request, cancellationToken));
                });
        }
    }

    internal static class StreamingHandlers
    {
        private static readonly string[] WeatherSummaries =
        [
            "Sunny",
            "Cloudy",
            "Overcast",
            "Rain",
            "Windy",
            "Humid",
            "Foggy"
        ];

        public static ValueTask<Result<IStreamCall>> HandleWeatherStreamAsync(
            JsonCodec<WeatherStreamRequest, WeatherUpdate> codec,
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken)
        {
            var decode = codec.DecodeRequest(request.Body, request.Meta);
            if (decode.IsFailure)
            {
                return ValueTask.FromResult(Err<IStreamCall>(decode.Error!));
            }

            var streamRequest = decode.Value;
            var call = ServerStreamCall.Create(request.Meta, new ResponseMeta(encoding: codec.Encoding));

            _ = Task.Run(
                () => PumpWeatherUpdatesAsync(codec, call, streamRequest, request.Meta.Transport, cancellationToken),
                CancellationToken.None);

            return ValueTask.FromResult(Ok((IStreamCall)call));
        }

        private static async Task PumpWeatherUpdatesAsync(
            JsonCodec<WeatherStreamRequest, WeatherUpdate> codec,
            ServerStreamCall call,
            WeatherStreamRequest streamRequest,
            string? transport,
            CancellationToken cancellationToken)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, streamRequest.IntervalSeconds));
            var totalMessages = Math.Max(1, streamRequest.Count);

            try
            {
                for (var index = 0; index < totalMessages; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var summary = WeatherSummaries[index % WeatherSummaries.Length];
                    var temperature = Random.Shared.Next(-10, 35);
                    var update = new WeatherUpdate(
                        streamRequest.Location,
                        index + 1,
                        summary,
                        temperature,
                        DateTimeOffset.UtcNow);

                    var encode = codec.EncodeResponse(update, call.ResponseMeta);
                    if (encode.IsFailure)
                    {
                        await call.CompleteAsync(encode.Error!, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await call.WriteAsync(new ReadOnlyMemory<byte>(encode.Value), cancellationToken).ConfigureAwait(false);
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }

                await call.CompleteAsync(null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await call.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var error = OmniRelayErrorAdapter.FromStatus(
                    OmniRelayStatusCode.Internal,
                    string.IsNullOrWhiteSpace(ex.Message) ? "Streaming pipeline failed." : ex.Message,
                    transport ?? "stream");
                await call.CompleteAsync(error, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal sealed class ConsoleLoggingMiddleware :
        IUnaryInboundMiddleware,
        IOnewayInboundMiddleware,
        IStreamInboundMiddleware
    {
        public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken,
            UnaryInboundHandler next)
        {
            var stopwatch = Stopwatch.GetTimestamp();
            Console.WriteLine($"--> unary {request.Meta.Procedure} ({request.Meta.Transport ?? "transport?"})");

            var result = await next(request, cancellationToken).ConfigureAwait(false);

            var elapsed = Stopwatch.GetElapsedTime(stopwatch);
            if (result.IsSuccess)
            {
                Console.WriteLine($"<-- unary {request.Meta.Procedure} OK in {elapsed.TotalMilliseconds:F0} ms");
            }
            else
            {
                Console.WriteLine($"<-- unary {request.Meta.Procedure} ERROR: {result.Error?.Message ?? "unknown"}");
            }

            return result;
        }

        public async ValueTask<Result<OnewayAck>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken,
            OnewayInboundHandler next)
        {
            Console.WriteLine($"--> oneway {request.Meta.Procedure} ({request.Meta.Transport ?? "transport?"})");
            var result = await next(request, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                Console.WriteLine($"<-- oneway {request.Meta.Procedure} ACK");
            }
            else
            {
                Console.WriteLine($"<-- oneway {request.Meta.Procedure} ERROR: {result.Error?.Message ?? "unknown"}");
            }

            return result;
        }

        public async ValueTask<Result<IStreamCall>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            StreamCallOptions options,
            CancellationToken cancellationToken,
            StreamInboundHandler next)
        {
            Console.WriteLine($"--> stream {request.Meta.Procedure} ({options.Direction})");
            var result = await next(request, options, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                Console.WriteLine($"<-- stream {request.Meta.Procedure} started");
            }
            else
            {
                Console.WriteLine($"<-- stream {request.Meta.Procedure} ERROR: {result.Error?.Message ?? "unknown"}");
            }

            return result;
        }
    }

    internal readonly record struct SampleRuntime(
        Dispatcher.Dispatcher Dispatcher,
        HttpInbound HttpInbound,
        GrpcInbound GrpcInbound)
    {
        public Dispatcher.Dispatcher Dispatcher { get; init; } = Dispatcher;

        public HttpInbound HttpInbound { get; init; } = HttpInbound;

        public GrpcInbound GrpcInbound { get; init; } = GrpcInbound;
    }

    internal sealed record GreetRequest(string Name)
    {
        public string Name { get; init; } = Name;
    }

    internal sealed record GreetResponse(string Message, string Transport, DateTimeOffset IssuedAt)
    {
        public string Message { get; init; } = Message;

        public string Transport { get; init; } = Transport;

        public DateTimeOffset IssuedAt { get; init; } = IssuedAt;
    }

    internal sealed record TelemetryEvent(string Level, string Area, string Message)
    {
        public string Level { get; init; } = Level;

        public string Area { get; init; } = Area;

        public string Message { get; init; } = Message;
    }

    internal sealed record WeatherStreamRequest(string Location, int Count = 5, int IntervalSeconds = 1)
    {
        public string Location { get; init; } = Location;

        public int Count { get; init; } = Count;

        public int IntervalSeconds { get; init; } = IntervalSeconds;
    }

    internal sealed record WeatherUpdate(string Location, int Sequence, string Summary, int TemperatureC, DateTimeOffset Timestamp)
    {
        public string Location { get; init; } = Location;

        public int Sequence { get; init; } = Sequence;

        public string Summary { get; init; } = Summary;

        public int TemperatureC { get; init; } = TemperatureC;

        public DateTimeOffset Timestamp { get; init; } = Timestamp;
    }
}
