using System.Collections.Immutable;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace OmniRelay.Transport.Grpc.Interceptors;

internal sealed class CompositeClientInterceptor(GrpcClientInterceptorRegistry registry, string service) : Interceptor
{
    private readonly GrpcClientInterceptorRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly string _service = service ?? string.Empty;

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        var interceptors = _registry.Resolve(_service, context.Method.FullName);
        return InvokeUnary(interceptors, 0, request, context, continuation);
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        var interceptors = _registry.Resolve(_service, context.Method.FullName);
        return InvokeClientStreaming(interceptors, 0, context, continuation);
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        var interceptors = _registry.Resolve(_service, context.Method.FullName);
        return InvokeServerStreaming(interceptors, 0, request, context, continuation);
    }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        var interceptors = _registry.Resolve(_service, context.Method.FullName);
        return InvokeDuplexStreaming(interceptors, 0, context, continuation);
    }

    private static AsyncUnaryCall<TResponse> InvokeUnary<TRequest, TResponse>(
        ImmutableArray<Interceptor> interceptors,
        int index,
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        if (index >= interceptors.Length)
        {
            return continuation(request, context);
        }

        var interceptor = interceptors[index];
        return interceptor.AsyncUnaryCall(
            request,
            context,
            (req, ctx) => InvokeUnary(interceptors, index + 1, req, ctx, continuation));
    }

    private static AsyncServerStreamingCall<TResponse> InvokeServerStreaming<TRequest, TResponse>(
        ImmutableArray<Interceptor> interceptors,
        int index,
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        if (index >= interceptors.Length)
        {
            return continuation(request, context);
        }

        var interceptor = interceptors[index];
        return interceptor.AsyncServerStreamingCall(
            request,
            context,
            (req, ctx) => InvokeServerStreaming(interceptors, index + 1, req, ctx, continuation));
    }

    private static AsyncClientStreamingCall<TRequest, TResponse> InvokeClientStreaming<TRequest, TResponse>(
        ImmutableArray<Interceptor> interceptors,
        int index,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        if (index >= interceptors.Length)
        {
            return continuation(context);
        }

        var interceptor = interceptors[index];
        return interceptor.AsyncClientStreamingCall(
            context,
            ctx => InvokeClientStreaming(interceptors, index + 1, ctx, continuation));
    }

    private static AsyncDuplexStreamingCall<TRequest, TResponse> InvokeDuplexStreaming<TRequest, TResponse>(
        ImmutableArray<Interceptor> interceptors,
        int index,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
        where TRequest : class
        where TResponse : class
    {
        if (index >= interceptors.Length)
        {
            return continuation(context);
        }

        var interceptor = interceptors[index];
        return interceptor.AsyncDuplexStreamingCall(
            context,
            ctx => InvokeDuplexStreaming(interceptors, index + 1, ctx, continuation));
    }
}
