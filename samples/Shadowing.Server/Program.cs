using System.Diagnostics;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;

namespace OmniRelay.Samples.Shadowing;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var runtime = ShadowingBootstrap.Build();

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        await runtime.Dispatcher.StartOrThrowAsync().ConfigureAwait(false);
        Console.WriteLine("OmniRelay tee outbound sample is running.");
        Console.WriteLine($" HTTP inbound: {string.Join(", ", runtime.HttpInbound.Urls)}");
        Console.WriteLine($" gRPC inbound: {string.Join(", ", runtime.GrpcInbound.Urls)}");
        Console.WriteLine();
        Console.WriteLine("Primary payments RPCs mirror to a shadow stack. Press Ctrl+C to exit.");

        try
        {
            await Task.Delay(Timeout.Infinite, shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on Ctrl+C
        }

        Console.WriteLine("Stopping dispatcher...");
        await runtime.Dispatcher.StopOrThrowAsync().ConfigureAwait(false);
        Console.WriteLine("Dispatcher stopped.");
    }
}

internal static class ShadowingBootstrap
{
    public static ShadowingRuntime Build()
    {
        const string serviceName = "samples.shadowing";

        var httpInbound = new HttpInbound(["http://127.0.0.1:7180"]);
        var grpcInbound = new GrpcInbound(["http://127.0.0.1:7190"]);

        var dispatcherOptions = new DispatcherOptions(serviceName);
        dispatcherOptions.AddLifecycle("http-inbound", httpInbound);
        dispatcherOptions.AddLifecycle("grpc-inbound", grpcInbound);

        var primaryPayments = new GrpcOutbound(
            [new Uri("http://127.0.0.1:20000")],
            remoteService: "payments");

        var shadowPayments = new GrpcOutbound(
            [new Uri("http://127.0.0.1:20010")],
            remoteService: "payments-shadow");

        var teePayments = new TeeUnaryOutbound(
            primaryPayments,
            shadowPayments,
            new TeeOptions
            {
                SampleRate = 0.20,
                ShadowOnSuccessOnly = true,
                ShadowHeaderName = "x-shadow-origin",
                ShadowHeaderValue = "payments-migration"
            });

        dispatcherOptions.AddUnaryOutbound("payments", null, teePayments);

        var primaryAudit = new HttpOutbound(
            new HttpClient(),
            new Uri("http://127.0.0.1:21000/yarpc/v1/audit::record"),
            disposeClient: true);

        var shadowAudit = new HttpOutbound(
            new HttpClient(),
            new Uri("http://127.0.0.1:21010/yarpc/v1/audit::record"),
            disposeClient: true);

        var teeAudit = new TeeOnewayOutbound(
            primaryAudit,
            shadowAudit,
            new TeeOptions
            {
                SampleRate = 1.0,
                ShadowOnSuccessOnly = false,
                ShadowHeaderName = "x-audit-shadow",
                ShadowHeaderValue = "beta-stack"
            });

        dispatcherOptions.AddOnewayOutbound("audit", null, teeAudit);

        var tracing = new ConsoleLoggingMiddleware();
        dispatcherOptions.UnaryInboundMiddleware.Add(tracing);
        dispatcherOptions.OnewayInboundMiddleware.Add(tracing);
        dispatcherOptions.UnaryOutboundMiddleware.Add(tracing);
        dispatcherOptions.OnewayOutboundMiddleware.Add(tracing);

        var dispatcher = new Dispatcher.Dispatcher(dispatcherOptions);
        ShadowingProcedures.Register(dispatcher);

        return new ShadowingRuntime(dispatcher, httpInbound, grpcInbound);
    }
}

internal static class ShadowingProcedures
{
    public static void Register(Dispatcher.Dispatcher dispatcher)
    {
        var paymentsCodec = new JsonCodec<SubmitPaymentRequest, PaymentReceipt>();
        var auditCodec = new JsonCodec<AuditLogEntry, object>();

        dispatcher.Codecs.RegisterOutbound("payments", "payments::authorize", ProcedureKind.Unary, paymentsCodec);
        dispatcher.Codecs.RegisterOutbound("audit", "audit::record", ProcedureKind.Oneway, auditCodec);

        var paymentsClient = dispatcher.CreateUnaryClient("payments", paymentsCodec);
        var auditClient = dispatcher.CreateOnewayClient("audit", auditCodec);

        dispatcher.RegisterJsonUnary<SubmitPaymentRequest, PaymentReceipt>(
            "payments::submit",
            async (context, request) =>
            {
                var outboundMeta = new RequestMeta(
                    service: "payments",
                    procedure: "payments::authorize",
                    caller: context.Meta.Caller ?? dispatcher.ServiceName,
                    headers:
                    [
                        new KeyValuePair<string, string>("x-session-id", request.SessionId),
                        new KeyValuePair<string, string>("x-shadow-origin", "payments::submit")
                    ]);

                var outboundRequest = new Request<SubmitPaymentRequest>(outboundMeta, request);
                var upstream = await paymentsClient.CallAsync(outboundRequest, context.CancellationToken).ConfigureAwait(false);
                if (upstream.IsFailure)
                {
                    var error = upstream.Error ?? OmniRelayErrorAdapter.FromStatus(
                        OmniRelayStatusCode.Internal,
                        "Primary payments call failed.",
                        context.Meta.Transport ?? "grpc");
                    var status = upstream.Error is null ? OmniRelayStatusCode.Internal : OmniRelayErrorAdapter.ToStatus(upstream.Error);
                    throw new OmniRelayException(status, error.Message, error, context.Meta.Transport ?? "grpc");
                }

                var receipt = new PaymentReceipt(
                    upstream.Value.Body.AuthorizationId,
                    upstream.Value.Body.Decision,
                    upstream.Value.Meta.Headers.TryGetValue("x-shadow-applied", out var toggle) ? toggle : "unknown");

                _ = Task.Run(async () =>
                {
                    var auditMeta = new RequestMeta(
                        service: "audit",
                        procedure: "audit::record",
                        headers:
                        [
                            new KeyValuePair<string, string>("x-audit-shadow", "beta-stack")
                        ]);

                    var entry = new AuditLogEntry(
                        request.MerchantId,
                        receipt.AuthorizationId,
                        receipt.Decision,
                        DateTimeOffset.UtcNow);

                    var ack = await auditClient.CallAsync(new Request<AuditLogEntry>(auditMeta, entry), context.CancellationToken)
                        .ConfigureAwait(false);

                    if (ack.IsFailure)
                    {
                        Console.WriteLine($"[audit] primary ack failed: {ack.Error?.Message ?? "unknown"}");
                    }
                }, context.CancellationToken);

                return Response<PaymentReceipt>.Create(receipt, upstream.Value.Meta);
            });

    }
}

internal sealed class ConsoleLoggingMiddleware :
    IUnaryInboundMiddleware,
    IOnewayInboundMiddleware,
    IUnaryOutboundMiddleware,
    IOnewayOutboundMiddleware
{
    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundHandler next)
    {
        var stopwatch = Stopwatch.GetTimestamp();
        Console.WriteLine($"[inbound] --> unary {request.Meta.Procedure}");
        var response = await next(request, cancellationToken).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(stopwatch);
        Console.WriteLine(response.IsSuccess
            ? $"[inbound] <-- unary {request.Meta.Procedure} OK in {elapsed.TotalMilliseconds:F0} ms"
            : $"[inbound] <-- unary {request.Meta.Procedure} ERROR {response.Error?.Message ?? "unknown"}");
        return response;
    }

    public async ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundHandler next)
    {
        Console.WriteLine($"[inbound] --> oneway {request.Meta.Procedure}");
        var ack = await next(request, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(ack.IsSuccess
            ? $"[inbound] <-- oneway {request.Meta.Procedure} ack"
            : $"[inbound] <-- oneway {request.Meta.Procedure} ERROR {ack.Error?.Message ?? "unknown"}");
        return ack;
    }

    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundHandler next)
    {
        var stopwatch = Stopwatch.GetTimestamp();
        Console.WriteLine($"[outbound] --> unary {request.Meta.Service}/{request.Meta.Procedure}");
        var response = await next(request, cancellationToken).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(stopwatch);
        Console.WriteLine(response.IsSuccess
            ? $"[outbound] <-- unary {request.Meta.Service}/{request.Meta.Procedure} OK in {elapsed.TotalMilliseconds:F0} ms"
            : $"[outbound] <-- unary {request.Meta.Service}/{request.Meta.Procedure} ERROR {response.Error?.Message ?? "unknown"}");
        return response;
    }

    public async ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayOutboundHandler next)
    {
        Console.WriteLine($"[outbound] --> oneway {request.Meta.Service}/{request.Meta.Procedure}");
        var ack = await next(request, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(ack.IsSuccess
            ? $"[outbound] <-- oneway {request.Meta.Service}/{request.Meta.Procedure} ack"
            : $"[outbound] <-- oneway {request.Meta.Service}/{request.Meta.Procedure} ERROR {ack.Error?.Message ?? "unknown"}");
        return ack;
    }
}

internal readonly record struct ShadowingRuntime(
    Dispatcher.Dispatcher Dispatcher,
    HttpInbound HttpInbound,
    GrpcInbound GrpcInbound)
{
    public Dispatcher.Dispatcher Dispatcher { get; init; } = Dispatcher;

    public HttpInbound HttpInbound { get; init; } = HttpInbound;

    public GrpcInbound GrpcInbound { get; init; } = GrpcInbound;
}

internal sealed record SubmitPaymentRequest(string SessionId, string MerchantId, decimal Amount, string Currency, string CardToken)
{
    public string SessionId { get; init; } = SessionId;

    public string MerchantId { get; init; } = MerchantId;

    public decimal Amount { get; init; } = Amount;

    public string Currency { get; init; } = Currency;

    public string CardToken { get; init; } = CardToken;
}

internal sealed record PaymentDecision(string AuthorizationId, string Decision)
{
    public string AuthorizationId { get; init; } = AuthorizationId;

    public string Decision { get; init; } = Decision;
}

internal sealed record PaymentReceipt(string AuthorizationId, string Decision, string ShadowReplica)
{
    public string AuthorizationId { get; init; } = AuthorizationId;

    public string Decision { get; init; } = Decision;

    public string ShadowReplica { get; init; } = ShadowReplica;
}

internal sealed record AuditLogEntry(string MerchantId, string AuthorizationId, string Decision, DateTimeOffset OccurredAt)
{
    public string MerchantId { get; init; } = MerchantId;

    public string AuthorizationId { get; init; } = AuthorizationId;

    public string Decision { get; init; } = Decision;

    public DateTimeOffset OccurredAt { get; init; } = OccurredAt;
}
