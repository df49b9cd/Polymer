using DistributedDemo.Contracts;
using Hugo;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Http;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using static Hugo.Go;

const string PrometheusPath = "/omnirelay/metrics";

var inbound = new HttpInbound(
    ["http://0.0.0.0:6080"],
    configureServices: services => ConfigureInboundMetrics(services, "audit", PrometheusPath),
    configureApp: app => app.MapPrometheusScrapingEndpoint(PrometheusPath));
var options = new DispatcherOptions("audit");
options.AddLifecycle("http", inbound);
options.OnewayInboundMiddleware.Add(new RpcMetricsMiddleware());
options.OnewayInboundMiddleware.Add(new ConsoleAuditLogger());

var dispatcher = new Dispatcher(options);
var codec = new JsonCodec<AuditRecord, object>();

dispatcher.RegisterOneway("audit::record", builder =>
{
    builder.WithEncoding(codec.Encoding);
    builder.Handle((request, cancellationToken) =>
    {
        var decode = codec.DecodeRequest(request.Body, request.Meta);
        if (decode.IsFailure)
        {
            return ValueTask.FromResult<Result<OnewayAck>>(Err<OnewayAck>(decode.Error!));
        }

        var record = decode.Value;
        Console.WriteLine($"[audit] {record.EventType} order={record.OrderId} details={record.Details}");
        return ValueTask.FromResult<Result<OnewayAck>>(Ok(OnewayAck.Ack(new ResponseMeta(encoding: codec.Encoding))));
    });
});

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    shutdown.Cancel();
};

await dispatcher.StartOrThrowAsync().ConfigureAwait(false);
Console.WriteLine(
    "Audit service listening on {0}",
    inbound.Urls.Count > 0 ? string.Join(", ", inbound.Urls) : "http://0.0.0.0:6080");

try
{
    await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    // expected
}

await dispatcher.StopOrThrowAsync().ConfigureAwait(false);

static void ConfigureInboundMetrics(IServiceCollection services, string serviceName, string scrapePath)
{
    services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(serviceName: serviceName))
        .WithMetrics(builder =>
        {
            builder.AddMeter(
                "OmniRelay.Core.Peers",
                "OmniRelay.Transport.Grpc",
                "OmniRelay.Transport.Http",
                "OmniRelay.Rpc",
                "Hugo.Go");

            builder.AddPrometheusExporter(options => options.ScrapeEndpointPath = scrapePath);
        });
}

internal sealed class ConsoleAuditLogger : IOnewayInboundMiddleware
{
    public async ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundHandler next)
    {
        Console.WriteLine($"[audit] --> {request.Meta.Procedure}");
        var result = await next(request, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(result.IsSuccess
            ? $"[audit] <-- {request.Meta.Procedure} ack"
            : $"[audit] <-- {request.Meta.Procedure} ERROR {result.Error?.Message ?? "unknown"}");
        return result;
    }
}
