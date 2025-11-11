using System.Threading.Channels;
using System.Threading.RateLimiting;
using Hugo;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core.Middleware;

/// <summary>
/// Applies concurrency rate limiting for all RPC shapes using System.Threading.RateLimiting.
/// </summary>
public sealed class RateLimitingMiddleware(RateLimitingOptions? options = null) :
    IUnaryInboundMiddleware,
    IUnaryOutboundMiddleware,
    IOnewayInboundMiddleware,
    IOnewayOutboundMiddleware,
    IStreamInboundMiddleware,
    IStreamOutboundMiddleware,
    IClientStreamInboundMiddleware,
    IClientStreamOutboundMiddleware,
    IDuplexInboundMiddleware,
    IDuplexOutboundMiddleware
{
    private readonly RateLimitingOptions _options = options ?? new RateLimitingOptions
    {
        Limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 1024,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        })
    };

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeCoreAsync(request.Meta, cancellationToken, () => next(request, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeCoreAsync(request.Meta, cancellationToken, () => next(request, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayOutboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeCoreAsync(request.Meta, cancellationToken, () => next(request, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeCoreAsync(request.Meta, cancellationToken, () => next(request, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamInboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        options = EnsureNotNull(options, nameof(options));

        next = EnsureNotNull(next, nameof(next));

        return InvokeStreamAsync(request.Meta, cancellationToken, () => next(request, options, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamOutboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        options = EnsureNotNull(options, nameof(options));

        next = EnsureNotNull(next, nameof(next));

        return InvokeStreamAsync(request.Meta, cancellationToken, () => next(request, options, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        ClientStreamRequestContext context,
        CancellationToken cancellationToken,
        ClientStreamInboundDelegate next)
    {
        next = EnsureNotNull(next, nameof(next));

        return InvokeCoreAsync(context.Meta, cancellationToken, () => next(context, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<IClientStreamTransportCall>> InvokeAsync(
        RequestMeta requestMeta,
        CancellationToken cancellationToken,
        ClientStreamOutboundDelegate next)
    {
        requestMeta = EnsureNotNull(requestMeta, nameof(requestMeta));
        next = EnsureNotNull(next, nameof(next));

        return InvokeClientStreamAsync(requestMeta, cancellationToken, () => next(requestMeta, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexInboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeDuplexAsync(request.Meta, cancellationToken, () => next(request, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexOutboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeDuplexAsync(request.Meta, cancellationToken, () => next(request, cancellationToken));
    }

    private async ValueTask<Result<T>> InvokeCoreAsync<T>(
        RequestMeta meta,
        CancellationToken cancellationToken,
        Func<ValueTask<Result<T>>> next)
    {
        var limiter = ResolveLimiter(meta);
        if (limiter is null)
        {
            return await next().ConfigureAwait(false);
        }

        using var lease = await limiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        if (!lease.IsAcquired)
        {
            return Err<T>(RateLimitExceeded(meta));
        }

        return await next().ConfigureAwait(false);
    }

    private async ValueTask<Result<IStreamCall>> InvokeStreamAsync(
        RequestMeta meta,
        CancellationToken cancellationToken,
        Func<ValueTask<Result<IStreamCall>>> next)
    {
        var limiter = ResolveLimiter(meta);
        if (limiter is null)
        {
            return await next().ConfigureAwait(false);
        }

        var lease = await limiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        if (!lease.IsAcquired)
        {
            lease.Dispose();
            return Err<IStreamCall>(RateLimitExceeded(meta));
        }

        var result = await next().ConfigureAwait(false);
        if (result.IsFailure)
        {
            lease.Dispose();
            return result;
        }

        var wrapped = new RateLimitedStreamCall(result.Value, lease);
        return Ok<IStreamCall>(wrapped);
    }

    private async ValueTask<Result<IClientStreamTransportCall>> InvokeClientStreamAsync(
        RequestMeta meta,
        CancellationToken cancellationToken,
        Func<ValueTask<Result<IClientStreamTransportCall>>> next)
    {
        var limiter = ResolveLimiter(meta);
        if (limiter is null)
        {
            return await next().ConfigureAwait(false);
        }

        var lease = await limiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        if (!lease.IsAcquired)
        {
            lease.Dispose();
            return Err<IClientStreamTransportCall>(RateLimitExceeded(meta));
        }

        var result = await next().ConfigureAwait(false);
        if (result.IsFailure)
        {
            lease.Dispose();
            return result;
        }

        var wrapped = new RateLimitedClientStreamCall(result.Value, lease);
        return Ok<IClientStreamTransportCall>(wrapped);
    }

    private async ValueTask<Result<IDuplexStreamCall>> InvokeDuplexAsync(
        RequestMeta meta,
        CancellationToken cancellationToken,
        Func<ValueTask<Result<IDuplexStreamCall>>> next)
    {
        var limiter = ResolveLimiter(meta);
        if (limiter is null)
        {
            return await next().ConfigureAwait(false);
        }

        var lease = await limiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        if (!lease.IsAcquired)
        {
            lease.Dispose();
            return Err<IDuplexStreamCall>(RateLimitExceeded(meta));
        }

        var result = await next().ConfigureAwait(false);
        if (result.IsFailure)
        {
            lease.Dispose();
            return result;
        }

        var wrapped = new RateLimitedDuplexStreamCall(result.Value, lease);
        return Ok<IDuplexStreamCall>(wrapped);
    }

    private RateLimiter? ResolveLimiter(RequestMeta meta)
    {
        if (_options.LimiterSelector is { } selector)
        {
            var limiter = selector(meta);
            if (limiter is not null)
            {
                return limiter;
            }
        }

        return _options.Limiter;
    }

    private static Error RateLimitExceeded(RequestMeta meta)
    {
        var message = $"Rate limit exceeded for procedure '{meta.Procedure ?? "<unknown>"}'.";
        return OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.ResourceExhausted, message, transport: meta.Transport ?? "unknown");
    }

    private static T EnsureNotNull<T>(T? value, string paramName) where T : class
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        return value;
    }

    private sealed class RateLimitedStreamCall(IStreamCall inner, RateLimitLease lease) : IStreamCall
    {
        private readonly IStreamCall _inner = inner;
        private readonly RateLimitLease _lease = lease;

        public StreamDirection Direction => _inner.Direction;
        public RequestMeta RequestMeta => _inner.RequestMeta;
        public ResponseMeta ResponseMeta => _inner.ResponseMeta;
        public StreamCallContext Context => _inner.Context;
        public ChannelWriter<ReadOnlyMemory<byte>> Requests => _inner.Requests;
        public ChannelReader<ReadOnlyMemory<byte>> Responses => _inner.Responses;

        public ValueTask CompleteAsync(Error? error = null, CancellationToken cancellationToken = default) =>
            _inner.CompleteAsync(error, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _lease.Dispose();
            }
        }
    }

    private sealed class RateLimitedClientStreamCall(IClientStreamTransportCall inner, RateLimitLease lease) : IClientStreamTransportCall
    {
        private readonly IClientStreamTransportCall _inner = inner;
        private readonly RateLimitLease _lease = lease;

        public RequestMeta RequestMeta => _inner.RequestMeta;
        public ResponseMeta ResponseMeta => _inner.ResponseMeta;
        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> Response => _inner.Response;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(payload, cancellationToken);

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) =>
            _inner.CompleteAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _lease.Dispose();
            }
        }
    }

    private sealed class RateLimitedDuplexStreamCall(IDuplexStreamCall inner, RateLimitLease lease) : IDuplexStreamCall
    {
        private readonly IDuplexStreamCall _inner = inner;
        private readonly RateLimitLease _lease = lease;

        public RequestMeta RequestMeta => _inner.RequestMeta;
        public ResponseMeta ResponseMeta => _inner.ResponseMeta;
        public DuplexStreamCallContext Context => _inner.Context;
        public ChannelWriter<ReadOnlyMemory<byte>> RequestWriter => _inner.RequestWriter;
        public ChannelReader<ReadOnlyMemory<byte>> RequestReader => _inner.RequestReader;
        public ChannelWriter<ReadOnlyMemory<byte>> ResponseWriter => _inner.ResponseWriter;
        public ChannelReader<ReadOnlyMemory<byte>> ResponseReader => _inner.ResponseReader;

        public ValueTask CompleteRequestsAsync(Error? error = null, CancellationToken cancellationToken = default) =>
            _inner.CompleteRequestsAsync(error, cancellationToken);

        public ValueTask CompleteResponsesAsync(Error? error = null, CancellationToken cancellationToken = default) =>
            _inner.CompleteResponsesAsync(error, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _lease.Dispose();
            }
        }
    }
}

