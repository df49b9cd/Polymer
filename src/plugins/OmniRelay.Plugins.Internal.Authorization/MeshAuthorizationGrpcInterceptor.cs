using Grpc.Core;
using Grpc.Core.Interceptors;
using Hugo;
using OmniRelay.Authorization;
using static Hugo.Go;

namespace OmniRelay.Plugins.Internal.Authorization;

internal sealed class MeshAuthorizationGrpcInterceptor : Interceptor
{
    private readonly IMeshAuthorizationEvaluator _evaluator;

    public MeshAuthorizationGrpcInterceptor(IMeshAuthorizationEvaluator evaluator)
    {
        _evaluator = evaluator;
    }

    private Result<Unit> EnsureAuthorized(ServerCallContext context)
    {
        var httpContext = context.GetHttpContext();
        var decision = _evaluator.EvaluateResult("grpc", context.Method ?? string.Empty, httpContext);
        if (decision.IsFailure)
        {
            return Result.Fail<Unit>(decision.Error!);
        }

        if (!decision.Value.IsAllowed)
        {
            var message = decision.Value.Reason ?? "Authorization failed.";
            var error = Hugo.Error.From(message, "authorization.denied")
                .WithMetadata("transport", "grpc")
                .WithMetadata("procedure", context.Method ?? string.Empty);

            return Hugo.Result.Fail<Unit>(error);
        }

        return Hugo.Result.Ok(Unit.Value);
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        var auth = EnsureAuthorized(context);
        if (auth.IsFailure)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, auth.Error!.Message), new Metadata
            {
                { "omnirelay-error-code", auth.Error!.Code ?? "authorization.denied" }
            });
        }

        return await continuation(request, context).ConfigureAwait(false);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        var auth = EnsureAuthorized(context);
        if (auth.IsFailure)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, auth.Error!.Message), new Metadata
            {
                { "omnirelay-error-code", auth.Error!.Code ?? "authorization.denied" }
            });
        }

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
        var auth = EnsureAuthorized(context);
        if (auth.IsFailure)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, auth.Error!.Message), new Metadata
            {
                { "omnirelay-error-code", auth.Error!.Code ?? "authorization.denied" }
            });
        }

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
        var auth = EnsureAuthorized(context);
        if (auth.IsFailure)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, auth.Error!.Message), new Metadata
            {
                { "omnirelay-error-code", auth.Error!.Code ?? "authorization.denied" }
            });
        }

        await continuation(requestStream, responseStream, context).ConfigureAwait(false);
    }
}
