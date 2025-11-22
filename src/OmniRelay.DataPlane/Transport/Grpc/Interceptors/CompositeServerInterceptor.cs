using System.Collections.Immutable;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace OmniRelay.Transport.Grpc.Interceptors;

internal sealed class CompositeServerInterceptor(GrpcServerInterceptorRegistry registry) : Interceptor
{
    private readonly GrpcServerInterceptorRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        var interceptors = _registry.Resolve(context.Method);
        return await InvokeUnary(interceptors, 0, request, context, continuation).ConfigureAwait(false);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        var interceptors = _registry.Resolve(context.Method);
        await InvokeServerStreaming(interceptors, 0, request, responseStream, context, continuation).ConfigureAwait(false);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        var interceptors = _registry.Resolve(context.Method);
        return await InvokeClientStreaming(interceptors, 0, requestStream, context, continuation).ConfigureAwait(false);
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        var interceptors = _registry.Resolve(context.Method);
        await InvokeDuplexStreaming(interceptors, 0, requestStream, responseStream, context, continuation).ConfigureAwait(false);
    }

    private static Task<TResponse> InvokeUnary<TRequest, TResponse>(
        ImmutableArray<Interceptor> interceptors,
        int index,
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        if (index >= interceptors.Length)
        {
            return continuation(request, context);
        }

        var interceptor = interceptors[index];
        return interceptor.UnaryServerHandler(
            request,
            context,
            (req, ctx) => InvokeUnary(interceptors, index + 1, req, ctx, continuation));
    }

    private static Task InvokeServerStreaming<TRequest, TResponse>(
        ImmutableArray<Interceptor> interceptors,
        int index,
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        if (index >= interceptors.Length)
        {
            return continuation(request, responseStream, context);
        }

        var interceptor = interceptors[index];
        return interceptor.ServerStreamingServerHandler(
            request,
            responseStream,
            context,
            (req, writer, ctx) => InvokeServerStreaming(interceptors, index + 1, req, writer, ctx, continuation));
    }

    private static Task<TResponse> InvokeClientStreaming<TRequest, TResponse>(
        ImmutableArray<Interceptor> interceptors,
        int index,
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        if (index >= interceptors.Length)
        {
            return continuation(requestStream, context);
        }

        var interceptor = interceptors[index];
        return interceptor.ClientStreamingServerHandler(
            requestStream,
            context,
            (stream, ctx) => InvokeClientStreaming(interceptors, index + 1, stream, ctx, continuation));
    }

    private static Task InvokeDuplexStreaming<TRequest, TResponse>(
        ImmutableArray<Interceptor> interceptors,
        int index,
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        if (index >= interceptors.Length)
        {
            return continuation(requestStream, responseStream, context);
        }

        var interceptor = interceptors[index];
        return interceptor.DuplexStreamingServerHandler(
            requestStream,
            responseStream,
            context,
            (req, res, ctx) => InvokeDuplexStreaming(interceptors, index + 1, req, res, ctx, continuation));
    }
}
