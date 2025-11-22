using Grpc.Core;
using Grpc.Health.V1;
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
        while (!context.CancellationToken.IsCancellationRequested)
        {
            var response = await Check(request, context).ConfigureAwait(false);
            await responseStream.WriteAsync(response).ConfigureAwait(false);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), context.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
