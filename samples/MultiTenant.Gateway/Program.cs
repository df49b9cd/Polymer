using System.Collections.Concurrent;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Clients;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Transport.Http;
using static Hugo.Go;
using OmniRelayDispatcher = OmniRelay.Dispatcher.Dispatcher;

namespace OmniRelay.Samples.MultiTenant.Gateway;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var runtime = GatewayBootstrap.Build();

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        await runtime.Dispatcher.StartOrThrowAsync().ConfigureAwait(false);
        Console.WriteLine("Multi-tenant gateway running.");
        Console.WriteLine($" HTTP inbound: {string.Join(", ", runtime.HttpInbound.Urls)}");
        Console.WriteLine("Press Ctrl+C to stop.");

        try
        {
            await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        Console.WriteLine("Shutting down gateway...");
        await runtime.Dispatcher.StopOrThrowAsync().ConfigureAwait(false);
        Console.WriteLine("Gateway stopped.");
    }
}

internal static class GatewayBootstrap
{
    private static readonly TenantConfig[] Tenants =
    [
        new("tenant-a", new Uri("http://localhost:7201")),
        new("tenant-b", new Uri("http://localhost:7202"))
    ];

    public static GatewayRuntime Build()
    {
        var httpInbound = new HttpInbound(["http://127.0.0.1:7210"]);
        var options = new DispatcherOptions("samples.multi-tenant");
        options.AddLifecycle("http-inbound", httpInbound);

        foreach (var tenant in Tenants)
        {
            var outbound = new HttpOutbound(
                new HttpClient { BaseAddress = tenant.Backend },
                new Uri($"{tenant.Backend}/yarpc/v1/order::place"),
                disposeClient: true);

            options.AddUnaryOutbound(tenant.TenantId, null, outbound);
        }

        var quotas = new TenantQuotaMiddleware(new Dictionary<string, int>
        {
            ["tenant-a"] = 5,
            ["tenant-b"] = 3
        });

        options.UnaryInboundMiddleware.Add(new TenantLoggingMiddleware());
        options.UnaryInboundMiddleware.Add(quotas);

        var dispatcher = new OmniRelayDispatcher(options);
        TenantProcedures.Register(dispatcher, Tenants);

        return new GatewayRuntime(dispatcher, httpInbound);
    }
}

internal sealed record TenantConfig(string TenantId, Uri Backend)
{
    public string TenantId { get; init; } = TenantId;

    public Uri Backend { get; init; } = Backend;
}

internal sealed record GatewayRuntime(OmniRelayDispatcher Dispatcher, HttpInbound HttpInbound)
{
    public OmniRelayDispatcher Dispatcher { get; init; } = Dispatcher;

    public HttpInbound HttpInbound { get; init; } = HttpInbound;
}

internal static class TenantProcedures
{
    public static void Register(OmniRelayDispatcher dispatcher, IEnumerable<TenantConfig> tenants)
    {
        dispatcher.RegisterJsonUnary<OrderRequest, OrderResponse>(
            "order::place",
            async (context, request) =>
            {
                var tenant = context.RequestMeta.Headers.TryGetValue("x-tenant-id", out var value)
                    ? value
                    : null;

                if (string.IsNullOrWhiteSpace(tenant))
                {
                    return Response<OrderResponse>.Create(
                        new OrderResponse("missing tenant"),
                        new ResponseMeta().WithHeader("status", "400"));
                }

                var client = ResolveClient(context.Dispatcher, tenant);
                var meta = new RequestMeta(
                    service: tenant,
                    procedure: "order::place",
                    encoding: "application/json",
                    headers: context.RequestMeta.Headers);

                var result = await client.CallAsync(new Request<OrderRequest>(meta, request), context.CancellationToken)
                    .ConfigureAwait(false);

                if (result.IsFailure)
                {
                    return Response<OrderResponse>.Create(
                        new OrderResponse($"backend error: {result.Error!.Message}"),
                        new ResponseMeta().WithHeader("status", "502"));
                }

                return result.Value;
            });

        static UnaryClient<OrderRequest, OrderResponse> ResolveClient(OmniRelayDispatcher dispatcher, string tenantId)
        {
            return dispatcher.CreateJsonClient<OrderRequest, OrderResponse>(
                tenantId,
                "order::place",
                outboundKey: null,
                aliases: null);
        }
    }
}

internal sealed class TenantQuotaMiddleware : IUnaryInboundMiddleware
{
    private readonly Dictionary<string, int> _limits;
    private readonly ConcurrentDictionary<string, int> _usage = new();

    public TenantQuotaMiddleware(Dictionary<string, int> limits) => _limits = limits;

    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundHandler next)
    {
        var tenant = request.Meta.Headers.TryGetValue("x-tenant-id", out var value) ? value : null;
        if (tenant is null)
        {
            return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(
                OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.InvalidArgument, "Missing x-tenant-id.")));
        }

        if (!_limits.TryGetValue(tenant, out var limit))
        {
            return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(
                OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.PermissionDenied, $"Tenant '{tenant}' is not configured.")));
        }

        var count = _usage.AddOrUpdate(tenant, 1, (_, current) => current + 1);
        if (count > limit)
        {
            return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(
                OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.ResourceExhausted, $"Tenant '{tenant}' exceeded quota.")));
        }

        return next(request, cancellationToken);
    }
}

internal sealed class TenantLoggingMiddleware : IUnaryInboundMiddleware
{
    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundHandler next)
    {
        var tenant = request.Meta.Headers.TryGetValue("x-tenant-id", out var value) ? value : "unknown";
        Console.WriteLine($"[{tenant}] {request.Meta.Procedure} called");
        var result = await next(request, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"[{tenant}] {request.Meta.Procedure} status {(result.IsSuccess ? "OK" : result.Error?.Message ?? "error")}");
        return result;
    }
}

internal sealed record OrderRequest(string Portfolio, decimal Notional)
{
    public string Portfolio { get; init; } = Portfolio;

    public decimal Notional { get; init; } = Notional;
}

internal sealed record OrderResponse(string Status)
{
    public string Status { get; init; } = Status;
}
