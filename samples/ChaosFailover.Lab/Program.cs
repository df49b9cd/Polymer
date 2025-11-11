using System.Collections.Concurrent;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Http;
using OmniRelayDispatcher = OmniRelay.Dispatcher.Dispatcher;

namespace OmniRelay.Samples.ChaosFailover.Lab;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        await using var backendA = await ChaosBackend.StartAsync("backend-a", successRate: 0.9).ConfigureAwait(false);
        await using var backendB = await ChaosBackend.StartAsync("backend-b", successRate: 0.5).ConfigureAwait(false);

        var lab = ChaosLabBootstrap.Build(backendA, backendB);

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        await lab.Dispatcher.StartOrThrowAsync().ConfigureAwait(false);
        Console.WriteLine("Chaos & failover lab running.");
        Console.WriteLine($" HTTP inbound: {string.Join(", ", lab.HttpInbound.Urls)}");
        Console.WriteLine("Press Ctrl+C to stop.");

        var generator = Task.Run(() => TrafficGenerator.RunAsync(lab.Dispatcher, shutdown.Token));

        try
        {
            await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        shutdown.Cancel();
        await generator.ConfigureAwait(false);
        await lab.Dispatcher.StopOrThrowAsync().ConfigureAwait(false);
        Console.WriteLine("Lab stopped.");
    }
}

internal static class ChaosLabBootstrap
{
    public static ChaosLabRuntime Build(ChaosBackend primary, ChaosBackend secondary)
    {
        var httpInbound = new HttpInbound(["http://127.0.0.1:7230"]);

        var options = new DispatcherOptions("samples.chaos-lab");
        options.AddLifecycle("http-inbound", httpInbound);

        options.AddUnaryOutbound("primary", null, primary.Outbound);
        options.AddUnaryOutbound("secondary", null, secondary.Outbound);

        options.UnaryOutboundMiddleware.Add(new OutboundRetryMiddleware(3));
        options.UnaryOutboundMiddleware.Add(new OutboundDeadlineMiddleware(TimeSpan.FromSeconds(2)));

        var dispatcher = new OmniRelayDispatcher(options);
        dispatcher.RegisterJsonUnary<ChaosRequest, ChaosResponse>(
            "chaos::ping",
            async (context, request) =>
            {
                var target = request.UseSecondary ? "secondary" : "primary";
                var client = context.Dispatcher.CreateJsonClient<ChaosRequest, ChaosResponse>(target, "chaos::ping");
                var result = await client.CallAsync(
                    new Request<ChaosRequest>(context.RequestMeta.WithHeader("x-chaos-id", Guid.NewGuid().ToString("N")), request),
                    context.CancellationToken).ConfigureAwait(false);

                if (result.IsFailure)
                {
                    return Response<ChaosResponse>.Create(new ChaosResponse(target, "failed", result.Error?.Message ?? "unknown"));
                }

                return result.Value;
            });

        return new ChaosLabRuntime(dispatcher, httpInbound);
    }
}

internal sealed record ChaosLabRuntime(OmniRelayDispatcher Dispatcher, HttpInbound HttpInbound)
{
    public OmniRelayDispatcher Dispatcher { get; init; } = Dispatcher;

    public HttpInbound HttpInbound { get; init; } = HttpInbound;
}

internal sealed class ChaosBackend : IAsyncDisposable
{
    private readonly OmniRelayDispatcher _dispatcher;
    private readonly ChaosUnaryOutbound _outbound;
    private bool _disposed;

    private ChaosBackend(OmniRelayDispatcher dispatcher, ChaosUnaryOutbound outbound)
    {
        _dispatcher = dispatcher;
        _outbound = outbound;
    }

    public IUnaryOutbound Outbound => _outbound;

    public static async Task<ChaosBackend> StartAsync(string name, double successRate)
    {
        var options = new DispatcherOptions(name);
        var dispatcher = new OmniRelayDispatcher(options);
        var outbound = new ChaosUnaryOutbound(dispatcher);

        dispatcher.RegisterJsonUnary<ChaosRequest, ChaosResponse>(
            "chaos::ping",
            (context, request) =>
            {
                var success = Random.Shared.NextDouble() < successRate;
                if (!success)
                {
                    throw new InvalidOperationException($"backend {name} failure");
                }

                return ValueTask.FromResult(Response<ChaosResponse>.Create(new ChaosResponse(name, "ok", null)));
            });

        await dispatcher.StartOrThrowAsync().ConfigureAwait(false);
        return new ChaosBackend(dispatcher, outbound);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _dispatcher.StopOrThrowAsync().ConfigureAwait(false);
    }
}

internal sealed class ChaosUnaryOutbound(OmniRelayDispatcher dispatcher) : IUnaryOutbound
{
    private readonly OmniRelayDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(IRequest<ReadOnlyMemory<byte>> request, CancellationToken cancellationToken = default)
    {
        var procedure = request.Meta.Procedure ?? "chaos::ping";
        return _dispatcher.InvokeUnaryAsync(procedure, request, cancellationToken);
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

internal static class TrafficGenerator
{
    public static async Task RunAsync(OmniRelayDispatcher dispatcher, CancellationToken cancellationToken)
    {
        var stats = new ConcurrentDictionary<string, int>();

        while (!cancellationToken.IsCancellationRequested)
        {
            var useSecondary = Random.Shared.NextDouble() < 0.2;
            var client = dispatcher.CreateJsonClient<ChaosRequest, ChaosResponse>("samples.chaos-lab", "chaos::ping");

            var meta = new RequestMeta(
                service: "samples.chaos-lab",
                procedure: "chaos::ping",
                caller: "traffic-generator");

            var request = Request<ChaosRequest>.Create(new ChaosRequest(useSecondary), meta);
            var result = await client.CallAsync(request, cancellationToken).ConfigureAwait(false);

            var key = useSecondary ? "secondary" : "primary";
            stats.AddOrUpdate(key, result.IsSuccess ? 1 : 0, (_, current) => current + (result.IsSuccess ? 1 : 0));

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed record ChaosRequest(bool UseSecondary)
{
    public bool UseSecondary { get; init; } = UseSecondary;
}

internal sealed record ChaosResponse(string Backend, string Status, string? Error)
{
    public string Backend { get; init; } = Backend;

    public string Status { get; init; } = Status;

    public string? Error { get; init; } = Error;
}

internal sealed class OutboundRetryMiddleware(int maxAttempts) : IUnaryOutboundMiddleware
{
    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundDelegate next)
    {
        var attempt = 0;
        Result<Response<ReadOnlyMemory<byte>>> result;

        do
        {
            result = await next(request, cancellationToken).ConfigureAwait(false);
            attempt++;
        } while (result.IsFailure && attempt < maxAttempts);

        return result;
    }
}

internal sealed class OutboundDeadlineMiddleware(TimeSpan timeout) : IUnaryOutboundMiddleware
{
    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundDelegate next)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return await next(request, cts.Token).ConfigureAwait(false);
    }
}
