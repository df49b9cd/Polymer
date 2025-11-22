using Grpc.Core;
using Grpc.Core.Interceptors;

namespace OmniRelay.Security.Authorization;

internal sealed class MeshAuthorizationGrpcInterceptor : Interceptor
{
    private readonly MeshAuthorizationEvaluator _evaluator;

    public MeshAuthorizationGrpcInterceptor(MeshAuthorizationEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    private void EnsureAuthorized(ServerCallContext context)
    {
        var httpContext = context.GetHttpContext();
        var decision = _evaluator.Evaluate("grpc", context.Method ?? string.Empty, httpContext);
        if (!decision.IsAllowed)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, decision.Reason ?? "Authorization failed."));
        }
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        EnsureAuthorized(context);
        return await continuation(request, context).ConfigureAwait(false);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        EnsureAuthorized(context);
        return await continuation(requestStream, context).ConfigureAwait(false);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        EnsureAuthorized(context);
        await continuation(request, responseStream, context).ConfigureAwait(false);
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        EnsureAuthorized(context);
        await continuation(requestStream, responseStream, context).ConfigureAwait(false);
    }
}
