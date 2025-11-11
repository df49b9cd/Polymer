using DistributedDemo.Inventory.Protos;
using DistributedDemo.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniRelay.Configuration;
using OmniRelay.Core;
using OmniRelay.Core.Clients;
using OmniRelay.Dispatcher;
using static Hugo.Go;
using OmniDispatcher = OmniRelay.Dispatcher.Dispatcher;

namespace DistributedDemo.Gateway;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((context, configuration) =>
            {
                // Resolve appsettings.json relative to the executing assembly/output folder so the app works
                // whether started from the project directory, solution root, or published container.
                var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                configuration.AddJsonFile(settingsPath, optional: false, reloadOnChange: true);
                configuration.AddEnvironmentVariables(prefix: "DDEMO_");
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            })
            .ConfigureServices((context, services) =>
            {
                // Diagnostic: print configured inbound URLs
                try
                {
                    var options = new OmniRelay.Configuration.Models.OmniRelayConfigurationOptions();
                    context.Configuration.GetSection("omnirelay").Bind(options);
                    var httpUrls = options.Inbounds?.Http?.SelectMany(h => h?.Urls ?? Array.Empty<string>())?.ToArray() ?? [];
                    var grpcUrls = options.Inbounds?.Grpc?.SelectMany(g => g?.Urls ?? Array.Empty<string>())?.ToArray() ?? [];
                    Console.WriteLine($"[GatewayStartup] HTTP Inbounds: {string.Join(", ", httpUrls)} | gRPC Inbounds: {string.Join(", ", grpcUrls)}");
                    var logger = services.BuildServiceProvider().GetService<ILoggerFactory>()?.CreateLogger("GatewayStartup");
                    logger?.LogInformation("Configured inbound URLs - HTTP: {HttpUrls}; gRPC: {GrpcUrls}", string.Join(", ", httpUrls), string.Join(", ", grpcUrls));
                }
                catch
                {
                    // ignore logging failures
                }

                services.AddOmniRelayDispatcher(context.Configuration.GetSection("omnirelay"));
                services.AddSingleton<CheckoutWorkflow>();
            })
            .Build();

        RegisterGatewayProcedures(host.Services);

        await host.RunAsync().ConfigureAwait(false);
    }

    private static void RegisterGatewayProcedures(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<OmniDispatcher>();
        var workflow = scope.ServiceProvider.GetRequiredService<CheckoutWorkflow>();
        var loggerFactory = scope.ServiceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger("GatewayRegistration");

        dispatcher.RegisterJsonUnary<CheckoutRequest, CheckoutResponse>(
            "checkout::create",
            async (context, request) =>
            {
                var response = await workflow.ExecuteAsync(request, context.CancellationToken).ConfigureAwait(false);
                return response;
            },
            configureProcedure: builder =>
            {
                builder.AddAliases(["checkout::submit"]);
            });

        var pingCodec = new JsonCodec<CheckoutRequest, CheckoutResponse>();
        dispatcher.RegisterUnary("checkout::echo", builder =>
        {
            builder.WithEncoding(pingCodec.Encoding);
            builder.Handle((request, _) =>
            {
                var decode = pingCodec.DecodeRequest(request.Body, request.Meta);
                if (decode.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(decode.Error!));
                }

                var requestPayload = decode.Value;
                var responsePayload = new CheckoutResponse(
                    requestPayload.OrderId,
                    Reserved: true,
                    ConfirmationId: "echo",
                    Message: "echo");

                var responseMeta = new ResponseMeta(encoding: pingCodec.Encoding);
                var encode = pingCodec.EncodeResponse(responsePayload, responseMeta);
                if (encode.IsFailure)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(encode.Error!));
                }

                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(new ReadOnlyMemory<byte>(encode.Value), responseMeta)));
            });
        });

        logger?.LogInformation("Gateway dispatcher '{Service}' registered checkout procedures.", dispatcher.ServiceName);
    }
}

internal sealed class CheckoutWorkflow
{
    private readonly UnaryClient<ReserveSkuRequest, ReserveSkuResponse> _inventoryClient;
    private readonly OnewayClient<AuditRecord> _auditClient;
    private readonly JsonCodec<AuditRecord, object> _auditCodec;
    private readonly string _inventoryEncoding;
    private readonly ILogger<CheckoutWorkflow> _logger;

    public CheckoutWorkflow(OmniDispatcher dispatcher, ILogger<CheckoutWorkflow> logger)
    {
        _logger = logger;

        var inventoryCodec = new ProtobufCodec<ReserveSkuRequest, ReserveSkuResponse>();
        dispatcher.Codecs.RegisterOutbound(
            "inventory",
            "inventory::reserve",
            ProcedureKind.Unary,
            inventoryCodec);
        _inventoryClient = dispatcher.CreateUnaryClient("inventory", inventoryCodec);
        _inventoryEncoding = inventoryCodec.Encoding;

        _auditCodec = new JsonCodec<AuditRecord, object>();
        dispatcher.Codecs.RegisterOutbound(
            "audit",
            "audit::record",
            ProcedureKind.Oneway,
            _auditCodec);
        _auditClient = dispatcher.CreateOnewayClient("audit", _auditCodec);
    }

    public async ValueTask<CheckoutResponse> ExecuteAsync(CheckoutRequest request, CancellationToken cancellationToken)
    {
        var reserveRequest = new ReserveSkuRequest
        {
            Sku = request.Sku,
            Quantity = request.Quantity,
            CorrelationId = request.OrderId
        };

        var inventoryMeta = new RequestMeta(
            service: "inventory",
            procedure: "inventory::reserve",
            caller: "gateway.checkout",
            encoding: _inventoryEncoding);

        var inventoryCall = await _inventoryClient.CallAsync(
            new Request<ReserveSkuRequest>(inventoryMeta, reserveRequest),
            cancellationToken).ConfigureAwait(false);

        if (inventoryCall.IsFailure)
        {
            _logger.LogWarning(
                "Inventory reserve failed for {OrderId}: {Message}",
                request.OrderId,
                inventoryCall.Error?.Message ?? "unknown");

            await PublishAuditAsync(
                request.OrderId,
                "inventory.error",
                inventoryCall.Error?.Message ?? "inventory call failed",
                cancellationToken).ConfigureAwait(false);

            return new CheckoutResponse(
                request.OrderId,
                false,
                ConfirmationId: string.Empty,
                Message: inventoryCall.Error?.Message ?? "Inventory error");
        }

        var reserveResponse = inventoryCall.Value.Body;
        var message = reserveResponse.Success
            ? $"Reserved {request.Quantity} units of {request.Sku}"
            : string.IsNullOrWhiteSpace(reserveResponse.Message)
                ? "Inventory declined reservation."
                : reserveResponse.Message;

        await PublishAuditAsync(
            request.OrderId,
            reserveResponse.Success ? "inventory.success" : "inventory.reject",
            message,
            cancellationToken).ConfigureAwait(false);

        return new CheckoutResponse(
            request.OrderId,
            reserveResponse.Success,
            reserveResponse.ConfirmationId,
            message);
    }

    private async ValueTask PublishAuditAsync(string orderId, string eventType, string details, CancellationToken cancellationToken)
    {
        var meta = new RequestMeta(
            service: "audit",
            procedure: "audit::record",
            caller: "gateway.checkout",
            encoding: _auditCodec.Encoding);

        var record = new AuditRecord(orderId, eventType, details, DateTimeOffset.UtcNow);
        var ack = await _auditClient.CallAsync(new Request<AuditRecord>(meta, record), cancellationToken).ConfigureAwait(false);
        if (ack.IsFailure)
        {
            _logger.LogWarning("Audit publishing failed for {OrderId}: {Message}", orderId, ack.Error?.Message ?? "unknown");
        }
    }
}
