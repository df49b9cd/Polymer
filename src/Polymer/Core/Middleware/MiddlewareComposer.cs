using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using Polymer.Core.Transport;

namespace Polymer.Core.Middleware;

public static class MiddlewareComposer
{
    public static UnaryOutboundDelegate ComposeUnaryOutbound(
        IReadOnlyList<IUnaryOutboundMiddleware>? middleware,
        UnaryOutboundDelegate terminal)
    {
        if (terminal is null)
        {
            throw new ArgumentNullException(nameof(terminal));
        }

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
        if (terminal is null)
        {
            throw new ArgumentNullException(nameof(terminal));
        }

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
        if (terminal is null)
        {
            throw new ArgumentNullException(nameof(terminal));
        }

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
        if (terminal is null)
        {
            throw new ArgumentNullException(nameof(terminal));
        }

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
        if (terminal is null)
        {
            throw new ArgumentNullException(nameof(terminal));
        }

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
        if (terminal is null)
        {
            throw new ArgumentNullException(nameof(terminal));
        }

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
        if (terminal is null)
        {
            throw new ArgumentNullException(nameof(terminal));
        }

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
        if (terminal is null)
        {
            throw new ArgumentNullException(nameof(terminal));
        }

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
        if (terminal is null)
        {
            throw new ArgumentNullException(nameof(terminal));
        }

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
        if (terminal is null)
        {
            throw new ArgumentNullException(nameof(terminal));
        }

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
