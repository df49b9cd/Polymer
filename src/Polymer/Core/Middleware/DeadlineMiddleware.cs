using System;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Errors;
using static Hugo.Go;

namespace Polymer.Core.Middleware;

public sealed class DeadlineMiddleware(DeadlineOptions? options = null) :
    IUnaryOutboundMiddleware,
    IOnewayOutboundMiddleware,
    IStreamOutboundMiddleware,
    IClientStreamOutboundMiddleware,
    IDuplexOutboundMiddleware,
    IUnaryInboundMiddleware,
    IOnewayInboundMiddleware,
    IStreamInboundMiddleware,
    IClientStreamInboundMiddleware,
    IDuplexInboundMiddleware
{
    private readonly DeadlineOptions _options = options ?? new DeadlineOptions();

    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundDelegate next) =>
        InvokeWithDeadlineAsync(
            request.Meta,
            cancellationToken,
            (token, linked) => next(request, linked));

    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundDelegate next) =>
        InvokeWithDeadlineAsync(
            request.Meta,
            cancellationToken,
            (token, linked) => next(request, linked));

    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayOutboundDelegate next) =>
        InvokeWithDeadlineAsync(
            request.Meta,
            cancellationToken,
            (token, linked) => next(request, linked));

    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundDelegate next) =>
        InvokeWithDeadlineAsync(
            request.Meta,
            cancellationToken,
            (token, linked) => next(request, linked));

    public ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamOutboundDelegate next) =>
        InvokeWithDeadlineAsync(
            request.Meta,
            cancellationToken,
            (token, linked) => next(request, options, linked));

    public ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamInboundDelegate next) =>
        InvokeWithDeadlineAsync(
            request.Meta,
            cancellationToken,
            (token, linked) => next(request, options, linked));

    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        ClientStreamRequestContext context,
        CancellationToken cancellationToken,
        ClientStreamInboundDelegate next) =>
        InvokeWithDeadlineAsync(
            context.Meta,
            cancellationToken,
            (token, linked) => next(context, linked));

    public ValueTask<Result<IClientStreamTransportCall>> InvokeAsync(
        RequestMeta requestMeta,
        CancellationToken cancellationToken,
        ClientStreamOutboundDelegate next) =>
        InvokeWithDeadlineAsync(
            requestMeta,
            cancellationToken,
            (token, linked) => next(requestMeta, linked));

    public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexOutboundDelegate next) =>
        InvokeWithDeadlineAsync(
            request.Meta,
            cancellationToken,
            (token, linked) => next(request, linked));

    public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexInboundDelegate next) =>
        InvokeWithDeadlineAsync(
            request.Meta,
            cancellationToken,
            (token, linked) => next(request, linked));

    private async ValueTask<Result<T>> InvokeWithDeadlineAsync<T>(
        RequestMeta meta,
        CancellationToken cancellationToken,
        Func<CancellationToken, CancellationToken, ValueTask<Result<T>>> next)
    {
        using var linked = CreateDeadlineToken(meta, cancellationToken, out var deadlineExceeded);
        if (deadlineExceeded)
        {
            return Err<T>(DeadlineExceeded(meta));
        }

        try
        {
            return await next(linked.Token, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (linked.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Err<T>(DeadlineExceeded(meta, ex));
        }
    }

    private CancellationTokenSource CreateDeadlineToken(
        RequestMeta meta,
        CancellationToken cancellationToken,
        out bool deadlineExceeded)
    {
        deadlineExceeded = false;

        var effectiveDeadline = ResolveAbsoluteDeadline(meta);
        if (effectiveDeadline is null)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        if (effectiveDeadline <= now)
        {
            deadlineExceeded = true;
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        if (_options.MinimumLeadTime is { } lead && effectiveDeadline.Value - now < lead)
        {
            deadlineExceeded = true;
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        var timeout = effectiveDeadline.Value - now;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        return cts;
    }

    private static DateTimeOffset? ResolveAbsoluteDeadline(RequestMeta meta)
    {
        if (meta.Deadline is { } absolute)
        {
            return absolute.ToUniversalTime();
        }

        if (meta.TimeToLive is { } ttl)
        {
            if (ttl <= TimeSpan.Zero)
            {
                return DateTimeOffset.UtcNow;
            }

            return DateTimeOffset.UtcNow.Add(ttl);
        }

        return null;
    }

    private static Error DeadlineExceeded(RequestMeta meta, Exception? exception = null)
    {
        var message = $"Deadline exceeded for procedure '{meta.Procedure ?? "<unknown>"}'.";
        var error = PolymerErrorAdapter.FromStatus(PolymerStatusCode.DeadlineExceeded, message, transport: meta.Transport ?? "unknown");

        if (exception is not null)
        {
            error = error.WithMetadata("exception_type", exception.GetType().FullName ?? exception.GetType().Name);
        }

        return error;
    }
}
