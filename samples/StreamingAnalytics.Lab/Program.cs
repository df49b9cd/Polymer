using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Clients;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Transport.Grpc;
using static Hugo.Go;
using OmniRelayDispatcher = OmniRelay.Dispatcher.Dispatcher;

#pragma warning disable CA2007
namespace OmniRelay.Samples.StreamingAnalytics;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var runtime = StreamingLabBootstrap.Build();

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        await runtime.Dispatcher.StartOrThrowAsync().ConfigureAwait(false);
        Console.WriteLine("Streaming Analytics Lab dispatcher ready.");
        Console.WriteLine($" gRPC inbound: {string.Join(", ", runtime.GrpcInbound.Urls)}");
        Console.WriteLine("Press Ctrl+C to stop.");

        try
        {
            var demo = new StreamingDemo(runtime);
            await demo.RunAsync(shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when Ctrl+C is pressed.
        }

        Console.WriteLine("Shutting down dispatcher...");
        await runtime.Dispatcher.StopOrThrowAsync().ConfigureAwait(false);
        Console.WriteLine("Streaming Analytics Lab stopped.");
    }
}

internal static class StreamingLabBootstrap
{
    public const string ServiceName = "samples.streaming-lab";
    public const string LoopbackOutboundKey = "loopback";

    public static StreamingLabRuntime Build()
    {
        var tickerCodec = new JsonCodec<TickerSubscription, TickerUpdate>();
        var metricsCodec = new ProtobufCodec<MetricSample, MetricAck>();
        var insightsCodec = new ProtobufCodec<InsightRequest, InsightSignal>();

        var grpcInbound = new GrpcInbound(["http://127.0.0.1:7190"]);

        var options = new DispatcherOptions(ServiceName);
        options.AddLifecycle("grpc-inbound", grpcInbound);

        var grpcOutbound = new GrpcOutbound([new Uri("http://127.0.0.1:7190")], ServiceName);
        options.AddStreamOutbound(ServiceName, LoopbackOutboundKey, grpcOutbound);
        options.AddClientStreamOutbound(ServiceName, LoopbackOutboundKey, grpcOutbound);
        options.AddDuplexOutbound(ServiceName, LoopbackOutboundKey, grpcOutbound);

        options.AddOutboundStreamCodec(ServiceName, "marketdata::ticker-stream", tickerCodec);
        options.AddOutboundClientStreamCodec(ServiceName, "metrics::aggregate", metricsCodec);
        options.AddOutboundDuplexCodec(ServiceName, "insights::collab", insightsCodec);

        var dispatcher = new OmniRelayDispatcher(options);
        StreamingHandlers.Register(dispatcher, tickerCodec, metricsCodec, insightsCodec);

        return new StreamingLabRuntime(
            dispatcher,
            grpcInbound,
            ServiceName,
            LoopbackOutboundKey,
            tickerCodec,
            metricsCodec,
            insightsCodec);
    }
}

internal sealed record StreamingLabRuntime(
    OmniRelayDispatcher Dispatcher,
    GrpcInbound GrpcInbound,
    string Service,
    string OutboundKey,
    JsonCodec<TickerSubscription, TickerUpdate> TickerCodec,
    ProtobufCodec<MetricSample, MetricAck> MetricsCodec,
    ProtobufCodec<InsightRequest, InsightSignal> InsightCodec)
{
    public OmniRelayDispatcher Dispatcher { get; init; } = Dispatcher;

    public GrpcInbound GrpcInbound { get; init; } = GrpcInbound;

    public string Service { get; init; } = Service;

    public string OutboundKey { get; init; } = OutboundKey;

    public JsonCodec<TickerSubscription, TickerUpdate> TickerCodec { get; init; } = TickerCodec;

    public ProtobufCodec<MetricSample, MetricAck> MetricsCodec { get; init; } = MetricsCodec;

    public ProtobufCodec<InsightRequest, InsightSignal> InsightCodec { get; init; } = InsightCodec;
}

internal static class StreamingHandlers
{
    public static void Register(
        OmniRelayDispatcher dispatcher,
        JsonCodec<TickerSubscription, TickerUpdate> tickerCodec,
        ProtobufCodec<MetricSample, MetricAck> metricsCodec,
        ProtobufCodec<InsightRequest, InsightSignal> insightCodec)
    {
        dispatcher.RegisterStream(
            "marketdata::ticker-stream",
            builder =>
            {
                builder.WithEncoding(tickerCodec.Encoding);
                builder.Handle((request, _, cancellationToken) =>
                    HandleTickerStreamAsync(tickerCodec, request, cancellationToken));
            });

        dispatcher.RegisterClientStream(
            "metrics::aggregate",
            builder =>
            {
                builder.WithEncoding(metricsCodec.Encoding);
                builder.Handle(ProtobufCallAdapters.CreateClientStreamHandler(
                    metricsCodec,
                    HandleMetricsAggregateAsync));
            });

        dispatcher.RegisterDuplex(
            "insights::collab",
            builder =>
            {
                builder.WithEncoding(insightCodec.Encoding);
                builder.Handle(ProtobufCallAdapters.CreateDuplexHandler(
                    insightCodec,
                    HandleInsightCollabAsync));
            });
    }

    private static ValueTask<Result<IStreamCall>> HandleTickerStreamAsync(
        JsonCodec<TickerSubscription, TickerUpdate> codec,
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken)
    {
        var decode = codec.DecodeRequest(request.Body, request.Meta);
        if (decode.IsFailure)
        {
            return ValueTask.FromResult(Err<IStreamCall>(decode.Error!));
        }

        var subscription = decode.Value;
        var call = ServerStreamCall.Create(request.Meta, new ResponseMeta(encoding: codec.Encoding));

        _ = Task.Run(async () =>
        {
            try
            {
                var interval = TimeSpan.FromMilliseconds(Math.Clamp(subscription.IntervalMs, 250, 2_000));
                var total = Math.Clamp(subscription.BatchSize, 1, 50);

                for (var i = 0; i < total && !cancellationToken.IsCancellationRequested; i++)
                {
                    var update = MarketDataGenerator.CreateUpdate(subscription.Symbol, i);
                    var encode = codec.EncodeResponse(update, call.ResponseMeta);
                    if (encode.IsFailure)
                    {
                        await call.CompleteAsync(encode.Error!, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await call.WriteAsync(new ReadOnlyMemory<byte>(encode.Value), cancellationToken).ConfigureAwait(false);
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }

                await call.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await call.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var error = OmniRelayErrors.FromException(ex, request.Meta.Transport ?? "stream");
                await call.CompleteAsync(error, cancellationToken).ConfigureAwait(false);
            }
        }, CancellationToken.None);

        return ValueTask.FromResult(Ok((IStreamCall)call));
    }

    private static async ValueTask<Response<MetricAck>> HandleMetricsAggregateAsync(
        ProtobufCallAdapters.ProtobufClientStreamContext<MetricSample, MetricAck> context,
        CancellationToken cancellationToken)
    {
        var accumulator = new MetricAccumulator();

        await foreach (var sampleResult in context.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            accumulator.Add(sampleResult.ValueOrThrow());
        }

        var ack = accumulator.ToAck();
        return Response<MetricAck>.Create(ack, new ResponseMeta());
    }

    private static async ValueTask HandleInsightCollabAsync(
        ProtobufCallAdapters.ProtobufDuplexStreamContext<InsightRequest, InsightSignal> context,
        CancellationToken cancellationToken)
    {
        var backlog = new List<InsightRequest>();

        await foreach (var requestResult in context.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var request = requestResult.ValueOrThrow();
            backlog.Add(request);

            var signal = new InsightSignal
            {
                Analyst = request.Analyst,
                Message = $"Topic '{request.Topic}' sentiment {request.Sentiment:F2}",
                Confidence = Math.Clamp(0.45 + (Math.Abs(request.Sentiment) * 0.4), 0, 1)
            };

            var writeResult = await context.WriteAsync(signal, cancellationToken).ConfigureAwait(false);
            writeResult.ThrowIfFailure();
        }

        var summary = new InsightSignal
        {
            Analyst = "collab-bot",
            Message = $"Processed {backlog.Count} topics across {backlog.Select(r => r.Analyst).Distinct().Count()} analysts.",
            Confidence = 0.72
        };

        var finalWrite = await context.WriteAsync(summary, cancellationToken).ConfigureAwait(false);
        finalWrite.ThrowIfFailure();
    }
}

internal sealed class StreamingDemo(StreamingLabRuntime runtime)
{
    private readonly StreamingLabRuntime _runtime = runtime;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var ticker = StreamTickersAsync(cancellationToken);
        var metrics = PushMetricsAsync(cancellationToken);
        var insights = CollaborateAsync(cancellationToken);

        await Task.WhenAll(ticker, metrics, insights).ConfigureAwait(false);
    }

    private async Task StreamTickersAsync(CancellationToken cancellationToken)
    {
        var client = _runtime.Dispatcher.CreateStreamClient<TickerSubscription, TickerUpdate>(
            _runtime.Service,
            _runtime.TickerCodec,
            _runtime.OutboundKey);

        while (!cancellationToken.IsCancellationRequested)
        {
            var subscription = new TickerSubscription("ESG-ETF", BatchSize: 5, IntervalMs: 600);
            var meta = new RequestMeta(
                _runtime.Service,
                procedure: "marketdata::ticker-stream",
                caller: "lab-ticker",
                encoding: _runtime.TickerCodec.Encoding);

            var request = Request<TickerSubscription>.Create(subscription, meta);
            var stream = client.CallAsync(request, new StreamCallOptions(StreamDirection.Server), cancellationToken);

            await foreach (var responseResult in stream.WithCancellation(cancellationToken))
            {
                var response = responseResult.ValueOrThrow();
                var update = response.Body;
                Console.WriteLine($"[Ticker] {update.Symbol}: {update.Price:F2} ({update.Delta:+0.00;-0.00}) #{update.Sequence}");
            }

            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PushMetricsAsync(CancellationToken cancellationToken)
    {
        var client = _runtime.Dispatcher.CreateClientStreamClient<MetricSample, MetricAck>(
            _runtime.Service,
            _runtime.MetricsCodec,
            _runtime.OutboundKey);

        var portfolios = new[] { "climate-alpha", "impact-beta" };
        var index = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var portfolio = portfolios[index++ % portfolios.Length];
            var meta = new RequestMeta(
                _runtime.Service,
                procedure: "metrics::aggregate",
                caller: "lab-metrics",
                encoding: _runtime.MetricsCodec.Encoding);

            var sessionResult = await client.StartAsync(meta, cancellationToken).ConfigureAwait(false);
            await using var session = sessionResult.ValueOrThrow();

            foreach (var sample in MetricBatchBuilder.Build(portfolio))
            {
                var writeResult = await session.WriteAsync(sample, cancellationToken).ConfigureAwait(false);
                writeResult.ThrowIfFailure();
            }

            await session.CompleteAsync(cancellationToken).ConfigureAwait(false);
            var responseResult = await session.Response.ConfigureAwait(false);
            var response = responseResult.ValueOrThrow();

            Console.WriteLine($"[Metrics] {response.Body.Portfolio} avg={response.Body.AverageScore:F3} exposure={response.Body.TotalExposure:F1} :: {response.Body.Note}");
            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CollaborateAsync(CancellationToken cancellationToken)
    {
        var client = _runtime.Dispatcher.CreateDuplexStreamClient<InsightRequest, InsightSignal>(
            _runtime.Service,
            _runtime.InsightCodec,
            _runtime.OutboundKey);

        while (!cancellationToken.IsCancellationRequested)
        {
            var meta = new RequestMeta(
                _runtime.Service,
                procedure: "insights::collab",
                caller: "lab-insights",
                encoding: _runtime.InsightCodec.Encoding);

            var sessionResult = await client.StartAsync(meta, cancellationToken).ConfigureAwait(false);
            await using var session = sessionResult.ValueOrThrow();

            var readerTask = ConsumeSignalsAsync(session, cancellationToken);

            foreach (var request in InsightScript.Build())
            {
                var writeResult = await session.WriteAsync(request, cancellationToken).ConfigureAwait(false);
                writeResult.ThrowIfFailure();
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
            }

            await session.CompleteRequestsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await readerTask.ConfigureAwait(false);

            await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ConsumeSignalsAsync(
        DuplexStreamClient<InsightRequest, InsightSignal>.DuplexStreamSession session,
        CancellationToken cancellationToken)
    {
        await foreach (var responseResult in session.ReadResponsesAsync(cancellationToken))
        {
            var response = responseResult.ValueOrThrow();
            var insight = response.Body;
            Console.WriteLine($"[Insights] {insight.Analyst}: {insight.Message} (confidence {insight.Confidence:P0})");
        }
    }
}

internal static class MarketDataGenerator
{
    private static readonly Dictionary<string, decimal> LastPrice = new(StringComparer.OrdinalIgnoreCase);

    public static TickerUpdate CreateUpdate(string symbol, int sequence)
    {
        var previous = LastPrice.TryGetValue(symbol, out var price) ? price : 100m;
        var delta = (decimal)(Random.Shared.NextDouble() - 0.5) * 0.75m;
        var next = Math.Max(1, previous + delta);
        LastPrice[symbol] = next;

        return new TickerUpdate(symbol, next, delta, sequence + 1, DateTimeOffset.UtcNow);
    }
}

internal sealed class MetricAccumulator
{
    private double _totalScore;
    private double _totalExposure;
    private int _count;
    private string? _portfolio;

    public void Add(MetricSample sample)
    {
        _portfolio ??= sample.Portfolio;
        _totalScore += sample.Score;
        _totalExposure += sample.Exposure;
        _count++;
    }

    public MetricAck ToAck()
    {
        var avg = _count == 0 ? 0 : _totalScore / _count;
        return new MetricAck
        {
            Portfolio = _portfolio ?? "unknown",
            AverageScore = avg,
            TotalExposure = _totalExposure,
            Note = _count == 0 ? "no samples" : $"{_count} samples aggregated"
        };
    }
}

internal static class MetricBatchBuilder
{
    public static IEnumerable<MetricSample> Build(string portfolio)
    {
        for (var i = 0; i < 5; i++)
        {
            yield return new MetricSample
            {
                Portfolio = portfolio,
                Score = 70 + Random.Shared.NextDouble() * 25,
                Exposure = 10 + Random.Shared.NextDouble() * 30
            };
        }
    }
}

internal static class InsightScript
{
    private static readonly string[] Topics =
    [
        "hydrogen",
        "carbon-intensity",
        "offshore-wind",
        "battery-supply"
    ];

    public static IEnumerable<InsightRequest> Build()
    {
        foreach (var topic in Topics.OrderBy(_ => Guid.NewGuid()))
        {
            yield return new InsightRequest
            {
                Analyst = Random.Shared.Next(0, 2) == 0 ? "ava" : "leo",
                Topic = topic,
                Sentiment = Math.Round(Random.Shared.NextDouble() * 2 - 1, 2)
            };
        }
    }
}

internal sealed record TickerSubscription(string Symbol, int BatchSize = 10, int IntervalMs = 500)
{
    public string Symbol { get; init; } = Symbol;

    public int BatchSize { get; init; } = BatchSize;

    public int IntervalMs { get; init; } = IntervalMs;
}

internal sealed record TickerUpdate(string Symbol, decimal Price, decimal Delta, int Sequence, DateTimeOffset AsOf)
{
    public string Symbol { get; init; } = Symbol;

    public decimal Price { get; init; } = Price;

    public decimal Delta { get; init; } = Delta;

    public int Sequence { get; init; } = Sequence;

    public DateTimeOffset AsOf { get; init; } = AsOf;
}
