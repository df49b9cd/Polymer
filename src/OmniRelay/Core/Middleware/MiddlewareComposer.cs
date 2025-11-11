using OmniRelay.Core.Transport;

namespace OmniRelay.Core.Middleware;

/// <summary>
/// Utility to compose middleware lists into executable delegate pipelines for each RPC shape.
/// </summary>
public static class MiddlewareComposer
{
    /// <summary>Composes a unary outbound middleware pipeline.</summary>
    public static UnaryOutboundHandler ComposeUnaryOutbound(
        IReadOnlyList<IUnaryOutboundMiddleware>? middleware,
        UnaryOutboundHandler terminal)
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

    /// <summary>Composes a unary inbound middleware pipeline.</summary>
    public static UnaryInboundHandler ComposeUnaryInbound(
        IReadOnlyList<IUnaryInboundMiddleware>? middleware,
        UnaryInboundHandler terminal)
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

    /// <summary>Composes an oneway outbound middleware pipeline.</summary>
    public static OnewayOutboundHandler ComposeOnewayOutbound(
        IReadOnlyList<IOnewayOutboundMiddleware>? middleware,
        OnewayOutboundHandler terminal)
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

    /// <summary>Composes an oneway inbound middleware pipeline.</summary>
    public static OnewayInboundHandler ComposeOnewayInbound(
        IReadOnlyList<IOnewayInboundMiddleware>? middleware,
        OnewayInboundHandler terminal)
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

    /// <summary>Composes a server-streaming outbound middleware pipeline.</summary>
    public static StreamOutboundHandler ComposeStreamOutbound(
        IReadOnlyList<IStreamOutboundMiddleware>? middleware,
        StreamOutboundHandler terminal)
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

    /// <summary>Composes a server-streaming inbound middleware pipeline.</summary>
    public static StreamInboundHandler ComposeStreamInbound(
        IReadOnlyList<IStreamInboundMiddleware>? middleware,
        StreamInboundHandler terminal)
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

    /// <summary>Composes a client-stream inbound middleware pipeline.</summary>
    public static ClientStreamInboundHandler ComposeClientStreamInbound(
        IReadOnlyList<IClientStreamInboundMiddleware>? middleware,
        ClientStreamInboundHandler terminal)
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

    /// <summary>Composes a client-stream outbound middleware pipeline.</summary>
    public static ClientStreamOutboundHandler ComposeClientStreamOutbound(
        IReadOnlyList<IClientStreamOutboundMiddleware>? middleware,
        ClientStreamOutboundHandler terminal)
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

    /// <summary>Composes a duplex inbound middleware pipeline.</summary>
    public static DuplexInboundHandler ComposeDuplexInbound(
        IReadOnlyList<IDuplexInboundMiddleware>? middleware,
        DuplexInboundHandler terminal)
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

    /// <summary>Composes a duplex outbound middleware pipeline.</summary>
    public static DuplexOutboundHandler ComposeDuplexOutbound(
        IReadOnlyList<IDuplexOutboundMiddleware>? middleware,
        DuplexOutboundHandler terminal)
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
