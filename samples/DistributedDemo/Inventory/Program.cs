using DistributedDemo.Inventory.Protos;
using Hugo;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using static Hugo.Go;

var instanceName = Environment.GetEnvironmentVariable("INSTANCE_NAME") ?? "primary";
var random = new Random(instanceName.GetHashCode(StringComparison.OrdinalIgnoreCase));

const string PrometheusPath = "/omnirelay/metrics";

var httpInbound = new HttpInbound(
    ["http://0.0.0.0:6080"],
    configureServices: services => ConfigureInboundMetrics(services, $"inventory-{instanceName}", PrometheusPath),
    configureApp: app => app.MapPrometheusScrapingEndpoint(PrometheusPath));
var grpcInbound = new GrpcInbound(["http://0.0.0.0:6101"]);

var options = new DispatcherOptions("inventory");
options.AddLifecycle("http", httpInbound);
options.AddLifecycle($"grpc-{instanceName}", grpcInbound);
options.UnaryInboundMiddleware.Add(new RpcMetricsMiddleware());
options.UnaryInboundMiddleware.Add(new ConsoleUnaryLogger(instanceName));

var dispatcher = new Dispatcher(options);
var codec = new ProtobufCodec<ReserveSkuRequest, ReserveSkuResponse>();

dispatcher.RegisterUnary("inventory::reserve", builder =>
{
    builder.WithEncoding(codec.Encoding);
    builder.Handle(async (request, cancellationToken) =>
    {
        var decode = codec.DecodeRequest(request.Body, request.Meta);
        if (decode.IsFailure)
        {
            return Err<Response<ReadOnlyMemory<byte>>>(decode.Error!);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(random.Next(10, 50)), cancellationToken).ConfigureAwait(false);

        var payload = decode.Value;
        var success = payload.Quantity <= 5 || random.NextDouble() > 0.2;
        var confirmationSegment = Guid.NewGuid().ToString("N").Substring(0, 8);

        var response = new ReserveSkuResponse
        {
            ConfirmationId = success ? $"{instanceName}-{confirmationSegment}" : string.Empty,
            Success = success,
            Message = success ? $"[{instanceName}] reserved {payload.Quantity}x{payload.Sku}" : $"[{instanceName}] insufficient stock"
        };

        var responseMeta = new ResponseMeta(encoding: codec.Encoding);
        var encode = codec.EncodeResponse(response, responseMeta);
        if (encode.IsFailure)
        {
            return Err<Response<ReadOnlyMemory<byte>>>(encode.Error!);
        }

        return Ok(Response<ReadOnlyMemory<byte>>.Create(new ReadOnlyMemory<byte>(encode.Value), responseMeta));
    });
});

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    shutdown.Cancel();
};

static string FormatUrls(IReadOnlyCollection<string> urls, params string[] fallback) =>
    urls.Count > 0 ? string.Join(", ", urls) : string.Join(", ", fallback);

await dispatcher.StartOrThrowAsync().ConfigureAwait(false);
Console.WriteLine(
    "Inventory instance '{0}' is listening on HTTP {1} and gRPC {2}.",
    instanceName,
    FormatUrls(httpInbound.Urls, "http://0.0.0.0:6080"),
    FormatUrls(grpcInbound.Urls, "http://0.0.0.0:6101"));

try
{
    await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    // expected on Ctrl+C
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

internal sealed class ConsoleUnaryLogger(string instance) : IUnaryInboundMiddleware
{
    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundHandler next)
    {
        Console.WriteLine($"[{instance}] --> {request.Meta.Procedure}");
        var result = await next(request, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(result.IsSuccess
            ? $"[{instance}] <-- {request.Meta.Procedure} OK"
            : $"[{instance}] <-- {request.Meta.Procedure} ERROR {result.Error?.Message ?? "unknown"}");
        return result;
    }
}
