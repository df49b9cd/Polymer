using System.Threading.Channels;
using System.Threading.RateLimiting;
using Hugo;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core.Middleware;

#pragma warning disable CA1068 // CancellationToken parameter precedes delegate for OmniRelay middleware contract.

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
        UnaryOutboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        return InvokeCoreAsync(request.Meta, cancellationToken, () => nextHandler(request, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        return InvokeCoreAsync(request.Meta, cancellationToken, () => nextHandler(request, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayOutboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        return InvokeCoreAsync(request.Meta, cancellationToken, () => nextHandler(request, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        return InvokeCoreAsync(request.Meta, cancellationToken, () => nextHandler(request, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamInboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        options = EnsureNotNull(options, nameof(options));

        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        return InvokeStreamAsync(request.Meta, cancellationToken, () => nextHandler(request, options, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamOutboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        options = EnsureNotNull(options, nameof(options));

        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        return InvokeStreamAsync(request.Meta, cancellationToken, () => nextHandler(request, options, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        ClientStreamRequestContext context,
        CancellationToken cancellationToken,
        ClientStreamInboundHandler nextHandler)
    {
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        return InvokeCoreAsync(context.Meta, cancellationToken, () => nextHandler(context, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<IClientStreamTransportCall>> InvokeAsync(
        RequestMeta requestMeta,
        CancellationToken cancellationToken,
        ClientStreamOutboundHandler nextHandler)
    {
        requestMeta = EnsureNotNull(requestMeta, nameof(requestMeta));
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        return InvokeClientStreamAsync(requestMeta, cancellationToken, () => nextHandler(requestMeta, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexInboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        return InvokeDuplexAsync(request.Meta, cancellationToken, () => nextHandler(request, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexOutboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        return InvokeDuplexAsync(request.Meta, cancellationToken, () => nextHandler(request, cancellationToken));
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

        public ValueTask CompleteAsync(Error? fault = null, CancellationToken cancellationToken = default) =>
            _inner.CompleteAsync(fault, cancellationToken);

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

        public ValueTask CompleteRequestsAsync(Error? fault = null, CancellationToken cancellationToken = default) =>
            _inner.CompleteRequestsAsync(fault, cancellationToken);

        public ValueTask CompleteResponsesAsync(Error? fault = null, CancellationToken cancellationToken = default) =>
            _inner.CompleteResponsesAsync(fault, cancellationToken);

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

#pragma warning restore CA1068
