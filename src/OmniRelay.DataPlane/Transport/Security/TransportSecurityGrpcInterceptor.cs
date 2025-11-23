using Grpc.Core;
using Grpc.Core.Interceptors;

namespace OmniRelay.Transport.Security;

/// <summary>gRPC interceptor that enforces the transport security policy.</summary>
public sealed class TransportSecurityGrpcInterceptor : Interceptor
{
    private readonly TransportSecurityPolicyEvaluator _evaluator;

    public TransportSecurityGrpcInterceptor(TransportSecurityPolicyEvaluator evaluator)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        var decision = _evaluator.Evaluate(TransportSecurityContext.FromServerCallContext("grpc", context));
        if (decision.IsFailure || !decision.Value.IsAllowed)
        {
            var statusDetail = decision.IsFailure ? decision.Error?.Message : decision.Value.Reason;
            var status = new Status(StatusCode.PermissionDenied, statusDetail ?? "Transport policy violation.");
            throw new RpcException(status, CreateTrailers(decision));
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
        var decision = _evaluator.Evaluate(TransportSecurityContext.FromServerCallContext("grpc", context));
        if (decision.IsFailure || !decision.Value.IsAllowed)
        {
            var statusDetail = decision.IsFailure ? decision.Error?.Message : decision.Value.Reason;
            var status = new Status(StatusCode.PermissionDenied, statusDetail ?? "Transport policy violation.");
            throw new RpcException(status, CreateTrailers(decision));
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
        var decision = _evaluator.Evaluate(TransportSecurityContext.FromServerCallContext("grpc", context));
        if (decision.IsFailure || !decision.Value.IsAllowed)
        {
            var statusDetail = decision.IsFailure ? decision.Error?.Message : decision.Value.Reason;
            var status = new Status(StatusCode.PermissionDenied, statusDetail ?? "Transport policy violation.");
            throw new RpcException(status, CreateTrailers(decision));
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
        var decision = _evaluator.Evaluate(TransportSecurityContext.FromServerCallContext("grpc", context));
        if (decision.IsFailure || !decision.Value.IsAllowed)
        {
            var statusDetail = decision.IsFailure ? decision.Error?.Message : decision.Value.Reason;
            var status = new Status(StatusCode.PermissionDenied, statusDetail ?? "Transport policy violation.");
            throw new RpcException(status, CreateTrailers(decision));
        }

        await continuation(requestStream, responseStream, context).ConfigureAwait(false);
    }

    private static Metadata CreateTrailers(Result<TransportSecurityDecision> decision)
    {
        var metadata = new Metadata();
        if (decision.IsFailure && decision.Error is not null)
        {
            if (!string.IsNullOrEmpty(decision.Error.Code))
            {
                metadata.Add("omnirelay-error-code", decision.Error.Code);
            }

            foreach (var (key, value) in decision.Error.Metadata)
            {
                metadata.Add($"omnirelay-error-{key}", value?.ToString() ?? string.Empty);
            }
        }

        return metadata;
    }
}
