using Hugo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;
using static Hugo.Go;
using OmniRelayDispatcher = OmniRelay.Dispatcher.Dispatcher;

namespace OmniRelay.Samples.MinimalApiBridge;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.WebHost.UseUrls("http://127.0.0.1:5058");

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<GreetingHandlers>();
        builder.Services.AddSingleton<PortfolioHandlers>();
        builder.Services.AddSingleton<ConsoleLoggingMiddleware>();
        builder.Services.AddSingleton<BridgeProcedureRegistrar>();

        builder.Services.AddSingleton(provider =>
        {
            var httpInbound = new HttpInbound(["http://127.0.0.1:7080"]);
            var grpcInbound = new GrpcInbound(["http://127.0.0.1:7090"]);

            var options = new DispatcherOptions("samples.minimal-api-bridge");
            options.AddLifecycle("http-inbound", httpInbound);
            options.AddLifecycle("grpc-inbound", grpcInbound);

            var logging = provider.GetRequiredService<ConsoleLoggingMiddleware>();
            options.UnaryInboundMiddleware.Add(logging);
            options.OnewayInboundMiddleware.Add(logging);

            var dispatcher = new OmniRelayDispatcher(options);
            provider.GetRequiredService<BridgeProcedureRegistrar>().Register(dispatcher);

            return new BridgeRuntime(dispatcher, httpInbound, grpcInbound);
        });

        builder.Services.AddSingleton(sp => sp.GetRequiredService<BridgeRuntime>().Dispatcher);
        builder.Services.AddHostedService<DispatcherHostedService>();

        var app = builder.Build();

        var runtime = app.Services.GetRequiredService<BridgeRuntime>();
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            app.Logger.LogInformation(
                "Minimal API host listening on {HttpHost}. OmniRelay HTTP inbound: {HttpInbound}. OmniRelay gRPC inbound: {GrpcInbound}.",
                string.Join(", ", app.Urls),
                string.Join(", ", runtime.HttpInbound.Urls),
                string.Join(", ", runtime.GrpcInbound.Urls));
        });

        app.MapGet("/api/dispatcher", () => Results.Json(new
        {
            runtime.Dispatcher.ServiceName,
            http = runtime.HttpInbound.Urls,
            grpc = runtime.GrpcInbound.Urls
        }));

        app.MapGet("/api/greetings/{name}", async (string name, GreetingHandlers handlers, CancellationToken ct) =>
        {
            var response = await handlers.CreateGreetingAsync(new GreetingRequest(name, Channel: "minimal-api"), ct)
                .ConfigureAwait(false);
            return Results.Ok(response);
        });

        app.MapPost("/api/portfolios/{portfolioId}/rebalance", async (string portfolioId, RebalanceRequest request, PortfolioHandlers handlers, CancellationToken ct) =>
        {
            var command = new RebalanceCommand(
                portfolioId,
                request.TargetEquityPercent,
                request.TargetFixedIncomePercent,
                Channel: "minimal-api",
                request.Notes);

            var plan = await handlers.CreatePlanAsync(command, ct).ConfigureAwait(false);
            return Results.Ok(plan);
        });

        app.MapPost("/api/alerts", (AlertEvent alert, PortfolioHandlers handlers) =>
        {
            handlers.RecordAlert(alert with { Channel = alert.Channel ?? "minimal-api" });
            return Results.Accepted(alert.CorrelationId ?? Guid.NewGuid().ToString("N"));
        });

        await app.RunAsync().ConfigureAwait(false);
    }
}

internal sealed record BridgeRuntime(OmniRelayDispatcher Dispatcher, HttpInbound HttpInbound, GrpcInbound GrpcInbound);

internal sealed class DispatcherHostedService(BridgeRuntime runtime, ILogger<DispatcherHostedService> logger) : IHostedService
{
    [SuppressMessage("Reliability", "CA2016:Forward the CancellationToken parameter", Justification = "Dispatcher start semantics do not accept caller tokens.")]
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await runtime.Dispatcher.StartOrThrowAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Started OmniRelay dispatcher ({Service}) - HTTP: {HttpInbound}; gRPC: {GrpcInbound}",
            runtime.Dispatcher.ServiceName,
            string.Join(", ", runtime.HttpInbound.Urls),
            string.Join(", ", runtime.GrpcInbound.Urls));
    }

    [SuppressMessage("Reliability", "CA2016:Forward the CancellationToken parameter", Justification = "Dispatcher stop semantics do not accept caller tokens.")]
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await runtime.Dispatcher.StopOrThrowAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Stopped OmniRelay dispatcher {Service}", runtime.Dispatcher.ServiceName);
    }
}

internal sealed class BridgeProcedureRegistrar(
    GreetingHandlers greetingHandlers,
    PortfolioHandlers portfolioHandlers,
    ILogger<BridgeProcedureRegistrar> logger)
{
    public void Register(OmniRelayDispatcher dispatcher)
    {
        dispatcher.RegisterJsonUnary<GreetingRequest, GreetingResponse>(
            "greetings::say",
            async (context, request) =>
            {
                var enriched = request with
                {
                    Channel = request.Channel ?? context.RequestMeta.Transport ?? "omnirelay"
                };

                var response = await greetingHandlers.CreateGreetingAsync(enriched, context.CancellationToken)
                    .ConfigureAwait(false);
                return Response<GreetingResponse>.Create(response);
            },
            configureProcedure: builder => builder.AddAliases(["greetings::hello"]));

        dispatcher.RegisterJsonUnary<RebalanceCommand, RebalancePlan>(
            "portfolio::rebalance",
            async (context, request) =>
            {
                var enriched = request with { Channel = request.Channel ?? "omnirelay" };
                var plan = await portfolioHandlers.CreatePlanAsync(enriched, context.CancellationToken)
                    .ConfigureAwait(false);
                return Response<RebalancePlan>.Create(plan);
            });

        var alertsCodec = new JsonCodec<AlertEvent, object?>();
        dispatcher.RegisterOneway(
            "alerts::emit",
            builder =>
            {
                builder.WithEncoding(alertsCodec.Encoding);
                builder.Handle((request, _) =>
                {
                    var decode = alertsCodec.DecodeRequest(request.Body, request.Meta);
                    if (decode.IsFailure)
                    {
                        return ValueTask.FromResult<Result<OnewayAck>>(Err<OnewayAck>(decode.Error!));
                    }

                    var alert = decode.Value with
                    {
                        Channel = decode.Value.Channel ?? request.Meta.Transport ?? "omnirelay"
                    };

                    portfolioHandlers.RecordAlert(alert);
                    var ack = OnewayAck.Ack(new ResponseMeta(encoding: alertsCodec.Encoding));
                    return ValueTask.FromResult<Result<OnewayAck>>(Ok(ack));
                });
            });

        logger.LogInformation("Registered bridge procedures for greetings, rebalance, and alerts.");
    }
}

internal sealed class GreetingHandlers(TimeProvider clock, ILogger<GreetingHandlers> logger)
{
    public ValueTask<GreetingResponse> CreateGreetingAsync(GreetingRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var issuedAt = clock.GetUtcNow();
        var channel = string.IsNullOrWhiteSpace(request.Channel) ? "unknown" : request.Channel;
        var response = new GreetingResponse(
            $"Hello {request.Name}!",
            channel,
            issuedAt,
            $"Handled by {channel}");

        logger.LogInformation(
            "Generated greeting for {Name} via {Channel} at {Timestamp}",
            request.Name,
            channel,
            issuedAt);

        return ValueTask.FromResult(response);
    }
}

internal sealed class PortfolioHandlers(TimeProvider clock, ILogger<PortfolioHandlers> logger)
{
    public async ValueTask<RebalancePlan> CreatePlanAsync(RebalanceCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);

        var issuedAt = clock.GetUtcNow();
        var actions = new List<RebalanceAction>
        {
            new("Equity", command.TargetEquityPercent >= 60 ? "Sell" : "Buy", Math.Abs(command.TargetEquityPercent - 60)),
            new("FixedIncome", command.TargetFixedIncomePercent >= 40 ? "Sell" : "Buy", Math.Abs(command.TargetFixedIncomePercent - 40))
        };

        var plan = new RebalancePlan(
            command.PortfolioId,
            command.TargetEquityPercent,
            command.TargetFixedIncomePercent,
            issuedAt,
            actions,
            command.Channel ?? "omni-relay");

        logger.LogInformation(
            "Generated rebalance plan for {PortfolioId} via {Channel}. Notes: {Notes}",
            command.PortfolioId,
            command.Channel ?? "unknown",
            command.Notes ?? "n/a");

        return plan;
    }

    public void RecordAlert(AlertEvent alert)
    {
        logger.LogWarning(
            "ALERT {Severity} from {Channel} ({CorrelationId}): {Message}",
            alert.Severity.ToUpperInvariant(),
            alert.Channel ?? "unknown",
            alert.CorrelationId ?? "n/a",
            alert.Message);
    }
}

internal sealed class ConsoleLoggingMiddleware(ILogger<ConsoleLoggingMiddleware> logger) :
    IUnaryInboundMiddleware,
    IOnewayInboundMiddleware
{
    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundDelegate next)
    {
        logger.LogInformation("[{Transport}] {Procedure} unary inbound", request.Meta.Transport ?? "unknown", request.Meta.Procedure ?? "unknown");
        return await next(request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundDelegate next)
    {
        logger.LogInformation("[{Transport}] {Procedure} oneway inbound", request.Meta.Transport ?? "unknown", request.Meta.Procedure ?? "unknown");
        return await next(request, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record GreetingRequest(string Name, string? Channel = null);

internal sealed record GreetingResponse(string Message, string Channel, DateTimeOffset IssuedAt, string Handler);

internal sealed record RebalanceRequest(decimal TargetEquityPercent, decimal TargetFixedIncomePercent, string? Notes);

internal sealed record RebalanceCommand(
    string PortfolioId,
    decimal TargetEquityPercent,
    decimal TargetFixedIncomePercent,
    string? Channel,
    string? Notes);

internal sealed record RebalancePlan(
    string PortfolioId,
    decimal TargetEquityPercent,
    decimal TargetFixedIncomePercent,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<RebalanceAction> Actions,
    string GeneratedBy);

internal sealed record RebalanceAction(string AssetClass, string Instruction, decimal Percent);

internal sealed record AlertEvent(string Severity, string Message, string? Channel, string? CorrelationId);
