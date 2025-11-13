using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace OmniRelay.IntegrationTests.Support;

public sealed class HostedProtocolCaptureInterceptor(ConcurrentQueue<string> observed) : Interceptor
{
    private readonly ConcurrentQueue<string> _observed = observed ?? throw new ArgumentNullException(nameof(observed));

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        RecordProtocol(context);
        return await base.UnaryServerHandler(request, context, continuation).ConfigureAwait(false);
    }

    private void RecordProtocol(ServerCallContext context)
    {
        var protocol = context.GetHttpContext()?.Request.Protocol ?? "unknown";
        _observed.Enqueue(protocol);
    }
}
