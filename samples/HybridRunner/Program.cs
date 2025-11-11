using System.Text.Json;
using System.Threading.Channels;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Transport.Http;
using static Hugo.Go;
using OmniRelayDispatcher = OmniRelay.Dispatcher.Dispatcher;

namespace OmniRelay.Samples.HybridRunner;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var runtime = HybridRunnerBootstrap.Build();

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        await runtime.Dispatcher.StartOrThrowAsync().ConfigureAwait(false);
        Console.WriteLine("Hybrid batch + realtime runner started.");
        Console.WriteLine($" HTTP inbound: {string.Join(", ", runtime.HttpInbound.Urls)}");
        Console.WriteLine("Press Ctrl+C to stop.");

        var worker = Task.Run(() => BatchWorker.RunAsync(runtime.Dispatcher, shutdown.Token));

        try
        {
            await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        shutdown.Cancel();
        await worker.ConfigureAwait(false);

        await runtime.Dispatcher.StopOrThrowAsync().ConfigureAwait(false);
        Console.WriteLine("Hybrid runner stopped.");
    }
}

internal static class HybridRunnerBootstrap
{
    public static HybridRunnerRuntime Build()
    {
        var httpInbound = new HttpInbound(["http://127.0.0.1:7220"]);
        var options = new DispatcherOptions("samples.hybrid-runner");
        options.AddLifecycle("http-inbound", httpInbound);

        options.OnewayInboundMiddleware.Add(new DeadlineMiddleware(TimeSpan.FromSeconds(10)));
        options.OnewayInboundMiddleware.Add(new RetryBudgetMiddleware(10));
        options.StreamInboundMiddleware.Add(new DashboardLoggingMiddleware());

        var dispatcher = new OmniRelayDispatcher(options);
        HybridProcedures.Register(dispatcher);

        return new HybridRunnerRuntime(dispatcher, httpInbound);
    }
}

internal sealed record HybridRunnerRuntime(OmniRelayDispatcher Dispatcher, HttpInbound HttpInbound);

internal static class HybridProcedures
{
    private static readonly Channel<BatchJob> JobQueue = Channel.CreateUnbounded<BatchJob>();
    private static readonly Channel<ProgressUpdate> Progress = Channel.CreateUnbounded<ProgressUpdate>();

    public static void Register(OmniRelayDispatcher dispatcher)
    {
        dispatcher.RegisterOneway(
            "batch::enqueue",
            builder =>
            {
                builder.WithEncoding("application/json");
                builder.Handle(async (request, _) =>
                {
                    var decode = JsonSerializer.Deserialize<BatchJob>(request.Body.Span);
                    if (decode is null)
                    {
                        return Err<OnewayAck>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.InvalidArgument, "Malformed payload."));
                    }

                    Console.WriteLine($"Enqueued job {decode.BatchId} for {decode.Customer}.");
                    await JobQueue.Writer.WriteAsync(decode, CancellationToken.None).ConfigureAwait(false);
                    return Ok(OnewayAck.Ack(new ResponseMeta(encoding: "application/json")));
                });
            });

        dispatcher.RegisterStream(
            "dashboard::stream",
            builder =>
            {
                builder.WithEncoding("application/json");
                builder.Handle((request, _, cancellationToken) =>
                    DashboardStream.CreateAsync(Progress.Reader, request, cancellationToken));
            });
    }

    public static ChannelReader<BatchJob> Jobs => JobQueue.Reader;

    public static ValueTask PublishProgressAsync(ProgressUpdate update, CancellationToken cancellationToken) =>
        Progress.Writer.WriteAsync(update, cancellationToken);
}

internal static class DashboardStream
{
    public static ValueTask<Result<IStreamCall>> CreateAsync(
        ChannelReader<ProgressUpdate> updates,
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken)
    {
        var call = ServerStreamCall.Create(request.Meta, new ResponseMeta(encoding: "application/json"));

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var update in updates.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    var payload = JsonSerializer.SerializeToUtf8Bytes(update);
                    await call.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore.
            }
            finally
            {
                await call.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }, CancellationToken.None);

        return ValueTask.FromResult(Ok((IStreamCall)call));
    }
}

internal static class BatchWorker
{
    public static async Task RunAsync(OmniRelayDispatcher dispatcher, CancellationToken cancellationToken)
    {
        await foreach (var job in HybridProcedures.Jobs.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            for (var i = 1; i <= job.Tasks; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                var update = new ProgressUpdate(job.BatchId, job.Customer, i, job.Tasks);
                await HybridProcedures.PublishProgressAsync(update, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

internal sealed record BatchJob(string BatchId, string Customer, int Tasks);
internal sealed record ProgressUpdate(string BatchId, string Customer, int Completed, int Total);

internal sealed class DeadlineMiddleware(TimeSpan deadline) : IOnewayInboundMiddleware
{
    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundDelegate next)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(deadline);
        return next(request, cts.Token);
    }
}

internal sealed class RetryBudgetMiddleware(int maxRetries) : IOnewayInboundMiddleware
{
    private readonly int _maxRetries = maxRetries;

    public async ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundDelegate next)
    {
        var attempts = 0;
        Result<OnewayAck> result;

        do
        {
            result = await next(request, cancellationToken).ConfigureAwait(false);
            attempts++;
        } while (result.IsFailure && attempts <= _maxRetries);

        return result;
    }
}

internal sealed class DashboardLoggingMiddleware : IStreamInboundMiddleware
{
    public async ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamInboundDelegate next)
    {
        Console.WriteLine($"[dashboard] stream requested from {request.Meta.Caller ?? "unknown"}");
        var result = await next(request, options, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            Console.WriteLine($"[dashboard] failed: {result.Error?.Message}");
        }
        return result;
    }
}
