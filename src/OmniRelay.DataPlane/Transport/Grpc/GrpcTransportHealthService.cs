using Grpc.Core;
using Grpc.Health.V1;
using Hugo;
using OmniRelay.Dispatcher;

namespace OmniRelay.Transport.Grpc;

internal sealed class GrpcTransportHealthService(Dispatcher.Dispatcher dispatcher, GrpcInbound inbound)
    : Health.HealthBase
{
    private readonly Dispatcher.Dispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    private readonly GrpcInbound _inbound = inbound ?? throw new ArgumentNullException(nameof(inbound));

    public override Task<HealthCheckResponse> Check(HealthCheckRequest request, ServerCallContext context)
    {
        var readiness = DispatcherHealthEvaluator.Evaluate(_dispatcher);
        var isServing = readiness.IsReady && !_inbound.IsDraining;

        var response = new HealthCheckResponse
        {
            Status = isServing
                ? HealthCheckResponse.Types.ServingStatus.Serving
                : HealthCheckResponse.Types.ServingStatus.NotServing
        };

        return Task.FromResult(response);
    }

    public override async Task Watch(
        HealthCheckRequest request,
        IServerStreamWriter<HealthCheckResponse> responseStream,
        ServerCallContext context)
    {
        var delay = TimeSpan.FromSeconds(5);
        while (!context.CancellationToken.IsCancellationRequested)
        {
            var response = await Check(request, context).ConfigureAwait(false);
            await responseStream.WriteAsync(response).ConfigureAwait(false);

            var delayResult = await DelayAsync(delay, context.CancellationToken).ConfigureAwait(false);
            if (delayResult.IsFailure && context.CancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static ValueTask<Result<Go.Unit>> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
        {
            return ValueTask.FromResult(Result.Ok(Go.Unit.Value));
        }

        return Result.TryAsync<Go.Unit>(async ct =>
        {
            await Go.DelayAsync(delay, TimeProvider.System, ct).ConfigureAwait(false);
            return Go.Unit.Value;
        }, cancellationToken: cancellationToken);
    }
}
