using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Hugo;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core.Middleware;

/// <summary>
/// Records request counts, durations, and outcomes for all RPC shapes using System.Diagnostics.Metrics.
/// </summary>
public sealed class RpcMetricsMiddleware :
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
    private static readonly Meter DefaultMeter = new("OmniRelay.Rpc");

    private readonly Counter<long> _requestCounter;
    private readonly Counter<long> _successCounter;
    private readonly Counter<long> _failureCounter;
    private readonly Histogram<double> _durationHistogram;
    private readonly RpcMetricsOptions _options;

    /// <summary>
    /// Creates the metrics middleware with optional configuration.
    /// </summary>
    public RpcMetricsMiddleware(RpcMetricsOptions? options = null)
    {
        _options = options ?? new RpcMetricsOptions();
        var meter = _options.Meter ?? DefaultMeter;
        var prefix = _options.MetricPrefix.TrimEnd('.');

        _requestCounter = meter.CreateCounter<long>($"{prefix}.requests", description: "Total RPC requests observed.");
        _successCounter = meter.CreateCounter<long>($"{prefix}.success", description: "Successful RPC completions.");
        _failureCounter = meter.CreateCounter<long>($"{prefix}.failure", description: "Failed RPC completions.");
        _durationHistogram = meter.CreateHistogram<double>($"{prefix}.duration", unit: "ms", description: "RPC duration in milliseconds.");
    }

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return ObserveUnaryAsync(
            "inbound",
            "unary",
            request.Meta,
            cancellationToken,
            (req, token) => next(req, token),
            () => request);
    }

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return ObserveUnaryAsync(
            "outbound",
            "unary",
            request.Meta,
            cancellationToken,
            (req, token) => next(req, token),
            () => request);
    }

    /// <inheritdoc />
    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return ObserveOnewayAsync(
            "inbound",
            "oneway",
            request.Meta,
            cancellationToken,
            (req, token) => next(req, token),
            () => request);
    }

    /// <inheritdoc />
    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayOutboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return ObserveOnewayAsync(
            "outbound",
            "oneway",
            request.Meta,
            cancellationToken,
            (req, token) => next(req, token),
            () => request);
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

        return ObserveStreamAsync(
            "inbound",
            $"stream.{options.Direction.ToString().ToLowerInvariant()}",
            request.Meta,
            cancellationToken,
            (req, callOptions, token) => next(req, callOptions, token),
            request,
            options);
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

        return ObserveStreamAsync(
            "outbound",
            $"stream.{options.Direction.ToString().ToLowerInvariant()}",
            request.Meta,
            cancellationToken,
            (req, callOptions, token) => next(req, callOptions, token),
            request,
            options);
    }

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        ClientStreamRequestContext context,
        CancellationToken cancellationToken,
        ClientStreamInboundDelegate next)
    {
        next = EnsureNotNull(next, nameof(next));

        return ObserveClientStreamInboundAsync(
            context.Meta,
            cancellationToken,
            context,
            (ctx, token) => next(ctx, token));
    }

    /// <inheritdoc />
    public ValueTask<Result<IClientStreamTransportCall>> InvokeAsync(
        RequestMeta requestMeta,
        CancellationToken cancellationToken,
        ClientStreamOutboundDelegate next)
    {
        requestMeta = EnsureNotNull(requestMeta, nameof(requestMeta));
        next = EnsureNotNull(next, nameof(next));

        return ObserveClientStreamOutboundAsync(
            requestMeta,
            cancellationToken,
            meta => next(meta, cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexInboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return ObserveDuplexAsync(
            "inbound",
            "duplex",
            request.Meta,
            cancellationToken,
            token => next(request, token));
    }

    /// <inheritdoc />
    public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexOutboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return ObserveDuplexAsync(
            "outbound",
            "duplex",
            request.Meta,
            cancellationToken,
            token => next(request, token));
    }

    private async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> ObserveUnaryAsync(
        string direction,
        string rpcType,
        RequestMeta meta,
        CancellationToken cancellationToken,
        Func<IRequest<ReadOnlyMemory<byte>>, CancellationToken, ValueTask<Result<Response<ReadOnlyMemory<byte>>>>> next,
        Func<IRequest<ReadOnlyMemory<byte>>> requestFactory)
    {
        var tags = CreateBaseTags(direction, rpcType, meta);
        _requestCounter.Add(1, tags);
        var stopwatch = Stopwatch.GetTimestamp();

        try
        {
            var result = await next(requestFactory(), cancellationToken).ConfigureAwait(false);
            var duration = Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds;

            if (result.IsSuccess)
            {
                RecordSuccess(duration, tags);
            }
            else
            {
                RecordFailure(duration, tags, result.Error!);
            }

            return result;
        }
        catch (Exception ex)
        {
            var duration = Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds;
            RecordException(duration, tags, ex);
            throw;
        }
    }

    private async ValueTask<Result<OnewayAck>> ObserveOnewayAsync(
        string direction,
        string rpcType,
        RequestMeta meta,
        CancellationToken cancellationToken,
        Func<IRequest<ReadOnlyMemory<byte>>, CancellationToken, ValueTask<Result<OnewayAck>>> next,
        Func<IRequest<ReadOnlyMemory<byte>>> requestFactory)
    {
        var tags = CreateBaseTags(direction, rpcType, meta);
        _requestCounter.Add(1, tags);
        var stopwatch = Stopwatch.GetTimestamp();

        try
        {
            var result = await next(requestFactory(), cancellationToken).ConfigureAwait(false);
            var duration = Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds;

            if (result.IsSuccess)
            {
                RecordSuccess(duration, tags);
            }
            else
            {
                RecordFailure(duration, tags, result.Error!);
            }

            return result;
        }
        catch (Exception ex)
        {
            var duration = Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds;
            RecordException(duration, tags, ex);
            throw;
        }
    }

    private async ValueTask<Result<IStreamCall>> ObserveStreamAsync(
        string direction,
        string rpcType,
        RequestMeta meta,
        CancellationToken cancellationToken,
        Func<IRequest<ReadOnlyMemory<byte>>, StreamCallOptions, CancellationToken, ValueTask<Result<IStreamCall>>> next,
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options)
    {
        var tags = CreateBaseTags(direction, rpcType, meta);
        _requestCounter.Add(1, tags);

        var stopwatch = Stopwatch.GetTimestamp();

        try
        {
            var result = await next(request, options, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                var durationFailure = Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds;
                RecordFailure(durationFailure, tags, result.Error!);
                return result;
            }

            var wrapped = new MetricsStreamCall(result.Value, tags, stopwatch, this);
            return Ok<IStreamCall>(wrapped);
        }
        catch (Exception ex)
        {
            var duration = Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds;
            RecordException(duration, tags, ex);
            throw;
        }
    }

    private async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> ObserveClientStreamInboundAsync(
        RequestMeta meta,
        CancellationToken cancellationToken,
        ClientStreamRequestContext context,
        Func<ClientStreamRequestContext, CancellationToken, ValueTask<Result<Response<ReadOnlyMemory<byte>>>>> next)
    {
        var tags = CreateBaseTags("inbound", "client_stream", meta);
        _requestCounter.Add(1, tags);
        var stopwatch = Stopwatch.GetTimestamp();

        try
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            var duration = Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds;

            if (result.IsSuccess)
            {
                RecordSuccess(duration, tags);
            }
            else
            {
                RecordFailure(duration, tags, result.Error!);
            }

            return result;
        }
        catch (Exception ex)
        {
            var duration = Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds;
            RecordException(duration, tags, ex);
            throw;
        }
    }

    private async ValueTask<Result<IClientStreamTransportCall>> ObserveClientStreamOutboundAsync(
        RequestMeta meta,
        CancellationToken cancellationToken,
        Func<RequestMeta, ValueTask<Result<IClientStreamTransportCall>>> next)
    {
        var tags = CreateBaseTags("outbound", "client_stream", meta);
        _requestCounter.Add(1, tags);
        var stopwatch = Stopwatch.GetTimestamp();

        try
        {
            var result = await next(meta).ConfigureAwait(false);
            if (result.IsFailure)
            {
                var durationFailure = Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds;
                RecordFailure(durationFailure, tags, result.Error!);
                return result;
            }

            var wrapped = new MetricsClientStreamTransportCall(result.Value, tags, stopwatch, this);
            return Ok<IClientStreamTransportCall>(wrapped);
        }
        catch (Exception ex)
        {
            var duration = Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds;
            RecordException(duration, tags, ex);
            throw;
        }
    }

    private async ValueTask<Result<IDuplexStreamCall>> ObserveDuplexAsync(
        string direction,
        string rpcType,
        RequestMeta meta,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<Result<IDuplexStreamCall>>> next)
    {
        var tags = CreateBaseTags(direction, rpcType, meta);
        _requestCounter.Add(1, tags);
        var stopwatch = Stopwatch.GetTimestamp();

        try
        {
            var result = await next(cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                var durationFailure = Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds;
                RecordFailure(durationFailure, tags, result.Error!);
                return result;
            }

            var wrapped = new MetricsDuplexStreamCall(result.Value, tags, stopwatch, this);
            return Ok<IDuplexStreamCall>(wrapped);
        }
        catch (Exception ex)
        {
            var duration = Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds;
            RecordException(duration, tags, ex);
            throw;
        }
    }

    private static KeyValuePair<string, object?>[] CreateBaseTags(string direction, string rpcType, RequestMeta meta) => [
            new KeyValuePair<string, object?>("rpc.direction", direction),
            new KeyValuePair<string, object?>("rpc.type", rpcType),
            new KeyValuePair<string, object?>("rpc.service", meta.Service),
            new KeyValuePair<string, object?>("rpc.procedure", meta.Procedure ?? string.Empty),
            new KeyValuePair<string, object?>("rpc.transport", meta.Transport ?? "unknown"),
            new KeyValuePair<string, object?>("rpc.encoding", meta.Encoding ?? "unknown"),
            new KeyValuePair<string, object?>("rpc.caller", meta.Caller ?? string.Empty)
        ];

    private void RecordSuccess(double durationMs, KeyValuePair<string, object?>[] tags)
    {
        _successCounter.Add(1, tags);
        _durationHistogram.Record(durationMs, tags);
    }

    private void RecordFailure(double durationMs, KeyValuePair<string, object?>[] tags, Error error)
    {
        var augmented = AppendStatus(tags, OmniRelayErrorAdapter.ToStatus(error).ToString());
        _failureCounter.Add(1, augmented);
        _durationHistogram.Record(durationMs, augmented);
    }

    private void RecordException(double durationMs, KeyValuePair<string, object?>[] tags, Exception exception)
    {
        var augmented = AppendStatus(tags, "exception");
        _failureCounter.Add(1, augmented);
        _durationHistogram.Record(durationMs, augmented);
    }

    private static KeyValuePair<string, object?>[] AppendStatus(KeyValuePair<string, object?>[] tags, string status)
    {
        var result = new KeyValuePair<string, object?>[tags.Length + 1];
        Array.Copy(tags, result, tags.Length);
        result[^1] = new KeyValuePair<string, object?>("rpc.status", status);
        return result;
    }

    private static T EnsureNotNull<T>(T? value, string paramName) where T : class
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        return value;
    }

    private sealed class MetricsStreamCall(IStreamCall inner, KeyValuePair<string, object?>[] tags, long startTimestamp, RpcMetricsMiddleware owner) : IStreamCall
    {
        private readonly IStreamCall _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        private readonly KeyValuePair<string, object?>[] _tags = tags ?? throw new ArgumentNullException(nameof(tags));
        private readonly long _startTimestamp = startTimestamp;
        private readonly RpcMetricsMiddleware _owner = owner;
        private Error? _completionError;

        public StreamDirection Direction => _inner.Direction;
        public RequestMeta RequestMeta => _inner.RequestMeta;
        public ResponseMeta ResponseMeta => _inner.ResponseMeta;
        public StreamCallContext Context => _inner.Context;
        public ChannelWriter<ReadOnlyMemory<byte>> Requests => _inner.Requests;
        public ChannelReader<ReadOnlyMemory<byte>> Responses => _inner.Responses;

        public async ValueTask CompleteAsync(Error? error = null, CancellationToken cancellationToken = default)
        {
            if (error is not null)
            {
                _completionError = error;
            }

            await _inner.CompleteAsync(error, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                var duration = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
                if (_completionError is null)
                {
                    _owner.RecordSuccess(duration, _tags);
                }
                else
                {
                    _owner.RecordFailure(duration, _tags, _completionError);
                }
            }
        }
    }

    private sealed class MetricsClientStreamTransportCall : IClientStreamTransportCall
    {
        private readonly IClientStreamTransportCall _inner;
        private readonly KeyValuePair<string, object?>[] _tags;
        private readonly long _startTimestamp;
        private readonly RpcMetricsMiddleware _owner;
        private readonly Lazy<Task<Result<Response<ReadOnlyMemory<byte>>>>> _response;

        public MetricsClientStreamTransportCall(
            IClientStreamTransportCall inner,
            KeyValuePair<string, object?>[] tags,
            long startTimestamp,
            RpcMetricsMiddleware owner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _tags = tags ?? throw new ArgumentNullException(nameof(tags));
            _startTimestamp = startTimestamp;
            _owner = owner;
            _response = new Lazy<Task<Result<Response<ReadOnlyMemory<byte>>>>>(ObserveResponseAsync);
        }

        public RequestMeta RequestMeta => _inner.RequestMeta;
        public ResponseMeta ResponseMeta => _inner.ResponseMeta;

        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> Response => new(_response.Value);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(payload, cancellationToken);

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) =>
            _inner.CompleteAsync(cancellationToken);

        public ValueTask DisposeAsync() =>
            _inner.DisposeAsync();

        private async Task<Result<Response<ReadOnlyMemory<byte>>>> ObserveResponseAsync()
        {
            try
            {
                var result = await _inner.Response.ConfigureAwait(false);
                var duration = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;

                if (result.IsSuccess)
                {
                    _owner.RecordSuccess(duration, _tags);
                }
                else
                {
                    _owner.RecordFailure(duration, _tags, result.Error!);
                }

                return result;
            }
            catch (Exception ex)
            {
                var duration = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
                _owner.RecordException(duration, _tags, ex);
                throw;
            }
        }
    }

    private sealed class MetricsDuplexStreamCall(IDuplexStreamCall inner, KeyValuePair<string, object?>[] tags, long startTimestamp, RpcMetricsMiddleware owner) : IDuplexStreamCall
    {
        private readonly IDuplexStreamCall _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        private readonly KeyValuePair<string, object?>[] _tags = tags ?? throw new ArgumentNullException(nameof(tags));
        private readonly long _startTimestamp = startTimestamp;
        private readonly RpcMetricsMiddleware _owner = owner;
        private Error? _requestError;
        private Error? _responseError;

        public RequestMeta RequestMeta => _inner.RequestMeta;
        public ResponseMeta ResponseMeta => _inner.ResponseMeta;
        public DuplexStreamCallContext Context => _inner.Context;
        public ChannelWriter<ReadOnlyMemory<byte>> RequestWriter => _inner.RequestWriter;
        public ChannelReader<ReadOnlyMemory<byte>> RequestReader => _inner.RequestReader;
        public ChannelWriter<ReadOnlyMemory<byte>> ResponseWriter => _inner.ResponseWriter;
        public ChannelReader<ReadOnlyMemory<byte>> ResponseReader => _inner.ResponseReader;

        public async ValueTask CompleteRequestsAsync(Error? error = null, CancellationToken cancellationToken = default)
        {
            if (error is not null)
            {
                _requestError = error;
            }

            await _inner.CompleteRequestsAsync(error, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask CompleteResponsesAsync(Error? error = null, CancellationToken cancellationToken = default)
        {
            if (error is not null)
            {
                _responseError = error;
            }

            await _inner.CompleteResponsesAsync(error, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                var duration = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
                var error = _responseError ?? _requestError;

                if (error is null)
                {
                    _owner.RecordSuccess(duration, _tags);
                }
                else
                {
                    _owner.RecordFailure(duration, _tags, error);
                }
            }
        }
    }
}

