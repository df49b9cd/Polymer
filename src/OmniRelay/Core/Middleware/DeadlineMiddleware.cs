using Hugo;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core.Middleware;

#pragma warning disable CA1068 // CancellationToken parameter precedes delegate for OmniRelay middleware contract.

/// <summary>
/// Applies deadlines from request metadata (absolute or TTL) by linking a cancellation token.
/// Works for unary, oneway, server/client/duplex streaming in both inbound and outbound directions.
/// </summary>
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

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundHandler next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeWithDeadlineAsync(
            request.Meta,
            cancellationToken,
            (_, linked) => next(request, linked));
    }

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundHandler next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeWithDeadlineAsync(
            request.Meta,
            cancellationToken,
            (_, linked) => next(request, linked));
    }

    /// <inheritdoc />
    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayOutboundHandler next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeWithDeadlineAsync(
            request.Meta,
            cancellationToken,
            (_, linked) => next(request, linked));
    }

    /// <inheritdoc />
    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundHandler next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeWithDeadlineAsync(
            request.Meta,
            cancellationToken,
            (_, linked) => next(request, linked));
    }

    /// <inheritdoc />
    public ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamOutboundHandler next)
    {
        request = EnsureNotNull(request, nameof(request));
        options = EnsureNotNull(options, nameof(options));

        next = EnsureNotNull(next, nameof(next));

        return InvokeWithDeadlineAsync(
            request.Meta,
            cancellationToken,
            (_, linked) => next(request, options, linked));
    }

    /// <inheritdoc />
    public ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamInboundHandler next)
    {
        request = EnsureNotNull(request, nameof(request));
        options = EnsureNotNull(options, nameof(options));

        next = EnsureNotNull(next, nameof(next));

        return InvokeWithDeadlineAsync(
            request.Meta,
            cancellationToken,
            (_, linked) => next(request, options, linked));
    }

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        ClientStreamRequestContext context,
        CancellationToken cancellationToken,
        ClientStreamInboundHandler next)
    {
        next = EnsureNotNull(next, nameof(next));

        return InvokeWithDeadlineAsync(
            context.Meta,
            cancellationToken,
            (_, linked) => next(context, linked));
    }

    /// <inheritdoc />
    public ValueTask<Result<IClientStreamTransportCall>> InvokeAsync(
        RequestMeta requestMeta,
        CancellationToken cancellationToken,
        ClientStreamOutboundHandler next)
    {
        requestMeta = EnsureNotNull(requestMeta, nameof(requestMeta));
        next = EnsureNotNull(next, nameof(next));

        return InvokeWithDeadlineAsync(
            requestMeta,
            cancellationToken,
            (_, linked) => next(requestMeta, linked));
    }

    /// <inheritdoc />
    public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexOutboundHandler next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeWithDeadlineAsync(
            request.Meta,
            cancellationToken,
            (_, linked) => next(request, linked));
    }

    /// <inheritdoc />
    public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexInboundHandler next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeWithDeadlineAsync(
            request.Meta,
            cancellationToken,
            (_, linked) => next(request, linked));
    }

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

    private static T EnsureNotNull<T>(T? value, string paramName) where T : class
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        return value;
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
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.DeadlineExceeded, message, transport: meta.Transport ?? "unknown");

        if (exception is not null)
        {
            error = error.WithMetadata("exception_type", exception.GetType().FullName ?? exception.GetType().Name);
        }

        return error;
    }
}

#pragma warning restore CA1068
