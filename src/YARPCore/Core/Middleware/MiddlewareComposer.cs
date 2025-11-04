using YARPCore.Core.Transport;

namespace YARPCore.Core.Middleware;

public static class MiddlewareComposer
{
    public static UnaryOutboundDelegate ComposeUnaryOutbound(
        IReadOnlyList<IUnaryOutboundMiddleware>? middleware,
        UnaryOutboundDelegate terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        if (middleware is null || middleware.Count == 0)
        {
            return terminal;
        }

        var next = terminal;

        for (var index = middleware.Count - 1; index >= 0; index--)
        {
            var middlewareInstance = middleware[index];
            var capturedNext = next;
            next = (request, cancellationToken) => middlewareInstance.InvokeAsync(request, cancellationToken, capturedNext);
        }

        return next;
    }

    public static UnaryInboundDelegate ComposeUnaryInbound(
        IReadOnlyList<IUnaryInboundMiddleware>? middleware,
        UnaryInboundDelegate terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        if (middleware is null || middleware.Count == 0)
        {
            return terminal;
        }

        var next = terminal;

        for (var index = middleware.Count - 1; index >= 0; index--)
        {
            var middlewareInstance = middleware[index];
            var capturedNext = next;
            next = (request, cancellationToken) => middlewareInstance.InvokeAsync(request, cancellationToken, capturedNext);
        }

        return next;
    }

    public static OnewayOutboundDelegate ComposeOnewayOutbound(
        IReadOnlyList<IOnewayOutboundMiddleware>? middleware,
        OnewayOutboundDelegate terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        if (middleware is null || middleware.Count == 0)
        {
            return terminal;
        }

        var next = terminal;

        for (var index = middleware.Count - 1; index >= 0; index--)
        {
            var middlewareInstance = middleware[index];
            var capturedNext = next;
            next = (request, cancellationToken) => middlewareInstance.InvokeAsync(request, cancellationToken, capturedNext);
        }

        return next;
    }

    public static OnewayInboundDelegate ComposeOnewayInbound(
        IReadOnlyList<IOnewayInboundMiddleware>? middleware,
        OnewayInboundDelegate terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        if (middleware is null || middleware.Count == 0)
        {
            return terminal;
        }

        var next = terminal;

        for (var index = middleware.Count - 1; index >= 0; index--)
        {
            var middlewareInstance = middleware[index];
            var capturedNext = next;
            next = (request, cancellationToken) => middlewareInstance.InvokeAsync(request, cancellationToken, capturedNext);
        }

        return next;
    }

    public static StreamOutboundDelegate ComposeStreamOutbound(
        IReadOnlyList<IStreamOutboundMiddleware>? middleware,
        StreamOutboundDelegate terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        if (middleware is null || middleware.Count == 0)
        {
            return terminal;
        }

        var next = terminal;

        for (var index = middleware.Count - 1; index >= 0; index--)
        {
            var middlewareInstance = middleware[index];
            var capturedNext = next;
            next = (request, options, cancellationToken) => middlewareInstance.InvokeAsync(request, options, cancellationToken, capturedNext);
        }

        return next;
    }

    public static StreamInboundDelegate ComposeStreamInbound(
        IReadOnlyList<IStreamInboundMiddleware>? middleware,
        StreamInboundDelegate terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        if (middleware is null || middleware.Count == 0)
        {
            return terminal;
        }

        var next = terminal;

        for (var index = middleware.Count - 1; index >= 0; index--)
        {
            var middlewareInstance = middleware[index];
            var capturedNext = next;
            next = (request, options, cancellationToken) => middlewareInstance.InvokeAsync(request, options, cancellationToken, capturedNext);
        }

        return next;
    }

    public static ClientStreamInboundDelegate ComposeClientStreamInbound(
        IReadOnlyList<IClientStreamInboundMiddleware>? middleware,
        ClientStreamInboundDelegate terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        if (middleware is null || middleware.Count == 0)
        {
            return terminal;
        }

        var next = terminal;

        for (var index = middleware.Count - 1; index >= 0; index--)
        {
            var middlewareInstance = middleware[index];
            var capturedNext = next;
            next = (context, cancellationToken) => middlewareInstance.InvokeAsync(context, cancellationToken, capturedNext);
        }

        return next;
    }

    public static ClientStreamOutboundDelegate ComposeClientStreamOutbound(
        IReadOnlyList<IClientStreamOutboundMiddleware>? middleware,
        ClientStreamOutboundDelegate terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        if (middleware is null || middleware.Count == 0)
        {
            return terminal;
        }

        var next = terminal;

        for (var index = middleware.Count - 1; index >= 0; index--)
        {
            var middlewareInstance = middleware[index];
            var capturedNext = next;
            next = (requestMeta, cancellationToken) => middlewareInstance.InvokeAsync(requestMeta, cancellationToken, capturedNext);
        }

        return next;
    }

    public static DuplexInboundDelegate ComposeDuplexInbound(
        IReadOnlyList<IDuplexInboundMiddleware>? middleware,
        DuplexInboundDelegate terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        if (middleware is null || middleware.Count == 0)
        {
            return terminal;
        }

        var next = terminal;

        for (var index = middleware.Count - 1; index >= 0; index--)
        {
            var middlewareInstance = middleware[index];
            var capturedNext = next;
            next = (request, cancellationToken) => middlewareInstance.InvokeAsync(request, cancellationToken, capturedNext);
        }

        return next;
    }

    public static DuplexOutboundDelegate ComposeDuplexOutbound(
        IReadOnlyList<IDuplexOutboundMiddleware>? middleware,
        DuplexOutboundDelegate terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        if (middleware is null || middleware.Count == 0)
        {
            return terminal;
        }

        var next = terminal;

        for (var index = middleware.Count - 1; index >= 0; index--)
        {
            var middlewareInstance = middleware[index];
            var capturedNext = next;
            next = (request, cancellationToken) => middlewareInstance.InvokeAsync(request, cancellationToken, capturedNext);
        }

        return next;
    }
}
