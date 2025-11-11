using System.Diagnostics;
using System.Threading.Channels;
using Hugo;
using OmniRelay.Core.Diagnostics;
using OmniRelay.Core.Transport;
using static Hugo.Go;

namespace OmniRelay.Core.Middleware;

/// <summary>
/// OpenTelemetry-based tracing middleware that creates activities for all RPC shapes,
/// supports inbound context extraction and outbound context injection, and honors runtime sampling.
/// </summary>
public sealed class RpcTracingMiddleware :
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
    private static readonly ActivitySource DefaultActivitySource = new("OmniRelay.Rpc");

    private readonly ActivitySource _activitySource;
    private readonly RpcTracingOptions _options;
    private readonly IDiagnosticsRuntime? _diagnosticsRuntime;

    /// <summary>
    /// Creates the tracing middleware with optional diagnostics runtime and options.
    /// </summary>
    public RpcTracingMiddleware(IDiagnosticsRuntime? diagnosticsRuntime = null, RpcTracingOptions? options = null)
    {
        _diagnosticsRuntime = diagnosticsRuntime;
        _options = options ?? new RpcTracingOptions();
        _activitySource = _options.ActivitySource ?? DefaultActivitySource;
    }

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeUnaryCoreAsync(
            "yarpcore.rpc.inbound.unary",
            ActivityKind.Server,
            request,
            cancellationToken,
            (req, token) => next(req, token),
            allowParentExtraction: true);
    }

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeUnaryCoreAsync(
            "yarpcore.rpc.outbound.unary",
            ActivityKind.Client,
            request,
            cancellationToken,
            (req, token) => next(req, token),
            allowParentExtraction: false);
    }

    /// <inheritdoc />
    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeOnewayCoreAsync(
            "yarpcore.rpc.inbound.oneway",
            ActivityKind.Server,
            request,
            cancellationToken,
            (req, token) => next(req, token),
            allowParentExtraction: true);
    }

    /// <inheritdoc />
    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayOutboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeOnewayCoreAsync(
            "yarpcore.rpc.outbound.oneway",
            ActivityKind.Client,
            request,
            cancellationToken,
            (req, token) => next(req, token),
            allowParentExtraction: false);
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

        return InvokeStreamCoreAsync(
            $"yarpcore.rpc.inbound.stream.{options.Direction.ToString().ToLowerInvariant()}",
            ActivityKind.Server,
            request,
            options,
            cancellationToken,
            (req, callOptions, token) => next(req, callOptions, token),
            allowParentExtraction: true);
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

        return InvokeStreamCoreAsync(
            $"yarpcore.rpc.outbound.stream.{options.Direction.ToString().ToLowerInvariant()}",
            ActivityKind.Client,
            request,
            options,
            cancellationToken,
            (req, callOptions, token) => next(req, callOptions, token),
            allowParentExtraction: false);
    }

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        ClientStreamRequestContext context,
        CancellationToken cancellationToken,
        ClientStreamInboundDelegate next)
    {
        next = EnsureNotNull(next, nameof(next));

        return InvokeClientStreamInboundAsync(
            "yarpcore.rpc.inbound.client_stream",
            context,
            cancellationToken,
            next);
    }

    /// <inheritdoc />
    public ValueTask<Result<IClientStreamTransportCall>> InvokeAsync(
        RequestMeta requestMeta,
        CancellationToken cancellationToken,
        ClientStreamOutboundDelegate next)
    {
        requestMeta = EnsureNotNull(requestMeta, nameof(requestMeta));
        next = EnsureNotNull(next, nameof(next));

        return InvokeClientStreamOutboundAsync(
            "yarpcore.rpc.outbound.client_stream",
            requestMeta,
            cancellationToken,
            next);
    }

    /// <inheritdoc />
    public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexInboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeDuplexCoreAsync(
            "yarpcore.rpc.inbound.duplex",
            ActivityKind.Server,
            request,
            cancellationToken,
            (req, token) => next(req, token),
            allowParentExtraction: true);
    }

    /// <inheritdoc />
    public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexOutboundDelegate next)
    {
        request = EnsureNotNull(request, nameof(request));
        next = EnsureNotNull(next, nameof(next));

        return InvokeDuplexCoreAsync(
            "yarpcore.rpc.outbound.duplex",
            ActivityKind.Client,
            request,
            cancellationToken,
            (req, token) => next(req, token),
            allowParentExtraction: false);
    }

    private async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeUnaryCoreAsync(
        string spanName,
        ActivityKind kind,
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        Func<IRequest<ReadOnlyMemory<byte>>, CancellationToken, ValueTask<Result<Response<ReadOnlyMemory<byte>>>>> next,
        bool allowParentExtraction)
    {
        var (activity, meta) = StartActivity(spanName, kind, request.Meta, allowParentExtraction);
        var effectiveRequest = ReferenceEquals(meta, request.Meta)
            ? request
            : new Request<ReadOnlyMemory<byte>>(meta, request.Body);

        try
        {
            var result = await next(effectiveRequest, cancellationToken).ConfigureAwait(false);
            if (activity is not null)
            {
                if (result.IsSuccess)
                {
                    SetSuccess(activity, result.Value.Meta.Encoding);
                }
                else
                {
                    RecordError(activity, result.Error!);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            throw;
        }
        finally
        {
            activity?.Stop();
        }
    }

    private async ValueTask<Result<OnewayAck>> InvokeOnewayCoreAsync(
        string spanName,
        ActivityKind kind,
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        Func<IRequest<ReadOnlyMemory<byte>>, CancellationToken, ValueTask<Result<OnewayAck>>> next,
        bool allowParentExtraction)
    {
        var (activity, meta) = StartActivity(spanName, kind, request.Meta, allowParentExtraction);
        var effectiveRequest = ReferenceEquals(meta, request.Meta)
            ? request
            : new Request<ReadOnlyMemory<byte>>(meta, request.Body);

        try
        {
            var result = await next(effectiveRequest, cancellationToken).ConfigureAwait(false);
            if (activity is not null)
            {
                if (result.IsSuccess)
                {
                    SetSuccess(activity, result.Value.Meta.Encoding);
                }
                else
                {
                    RecordError(activity, result.Error!);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            throw;
        }
        finally
        {
            activity?.Stop();
        }
    }

    private async ValueTask<Result<IStreamCall>> InvokeStreamCoreAsync(
        string spanName,
        ActivityKind kind,
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        Func<IRequest<ReadOnlyMemory<byte>>, StreamCallOptions, CancellationToken, ValueTask<Result<IStreamCall>>> next,
        bool allowParentExtraction)
    {
        var (activity, meta) = StartActivity(spanName, kind, request.Meta, allowParentExtraction);
        var effectiveRequest = ReferenceEquals(meta, request.Meta)
            ? request
            : new Request<ReadOnlyMemory<byte>>(meta, request.Body);

        try
        {
            var result = await next(effectiveRequest, options, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                if (activity is not null)
                {
                    RecordError(activity, result.Error!);
                }

                return result;
            }

            if (activity is null)
            {
                return result;
            }

            var wrapped = new TracingStreamCall(result.Value, activity);
            activity = null;
            return Ok<IStreamCall>(wrapped);
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            throw;
        }
        finally
        {
            activity?.Stop();
        }
    }

    private async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeClientStreamInboundAsync(
        string spanName,
        ClientStreamRequestContext context,
        CancellationToken cancellationToken,
        ClientStreamInboundDelegate next)
    {
        var (activity, _) = StartActivity(spanName, ActivityKind.Server, context.Meta, allowParentExtraction: true);

        try
        {
            var result = await next(context, cancellationToken).ConfigureAwait(false);
            if (activity is not null)
            {
                if (result.IsSuccess)
                {
                    SetSuccess(activity, result.Value.Meta.Encoding);
                }
                else
                {
                    RecordError(activity, result.Error!);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            throw;
        }
        finally
        {
            activity?.Stop();
        }
    }

    private async ValueTask<Result<IClientStreamTransportCall>> InvokeClientStreamOutboundAsync(
        string spanName,
        RequestMeta requestMeta,
        CancellationToken cancellationToken,
        ClientStreamOutboundDelegate next)
    {
        var (activity, meta) = StartActivity(spanName, ActivityKind.Client, requestMeta, allowParentExtraction: false);

        try
        {
            var result = await next(meta, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                if (activity is not null)
                {
                    RecordError(activity, result.Error!);
                }

                return result;
            }

            if (activity is null)
            {
                return result;
            }

            var wrapped = new TracingClientStreamTransportCall(result.Value, activity, this);
            activity = null;
            return Ok<IClientStreamTransportCall>(wrapped);
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            throw;
        }
        finally
        {
            activity?.Stop();
        }
    }

    private async ValueTask<Result<IDuplexStreamCall>> InvokeDuplexCoreAsync(
        string spanName,
        ActivityKind kind,
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        Func<IRequest<ReadOnlyMemory<byte>>, CancellationToken, ValueTask<Result<IDuplexStreamCall>>> next,
        bool allowParentExtraction)
    {
        var (activity, meta) = StartActivity(spanName, kind, request.Meta, allowParentExtraction);
        var effectiveRequest = ReferenceEquals(meta, request.Meta)
            ? request
            : new Request<ReadOnlyMemory<byte>>(meta, request.Body);

        try
        {
            var result = await next(effectiveRequest, cancellationToken).ConfigureAwait(false);
            if (result.IsFailure)
            {
                if (activity is not null)
                {
                    RecordError(activity, result.Error!);
                }

                return result;
            }

            if (activity is null)
            {
                return result;
            }

            var wrapped = new TracingDuplexStreamCall(result.Value, activity);
            activity = null;
            return Ok<IDuplexStreamCall>(wrapped);
        }
        catch (Exception ex)
        {
            RecordException(activity, ex);
            throw;
        }
        finally
        {
            activity?.Stop();
        }
    }

    private (Activity? Activity, RequestMeta Meta) StartActivity(
        string spanName,
        ActivityKind kind,
        RequestMeta meta,
        bool allowParentExtraction)
    {
        ActivityContext? parentContext = null;

        if (allowParentExtraction && _options.ExtractIncomingContext && TryExtractContext(meta, out var extractedContext))
        {
            parentContext = extractedContext;
        }

        if (_diagnosticsRuntime is { TraceSamplingProbability: double probability })
        {
            if (probability <= 0d)
            {
                return (null, meta);
            }

            if (probability < 1d)
            {
                var parentRecorded = parentContext.HasValue && parentContext.Value.TraceFlags.HasFlag(ActivityTraceFlags.Recorded);
                if (!parentRecorded)
                {
                    var sample = Random.Shared.NextDouble();
                    if (sample > probability)
                    {
                        return (null, meta);
                    }
                }
            }
        }

        Activity? activity = parentContext.HasValue
            ? _activitySource.StartActivity(spanName, kind, parentContext.Value)
            : _activitySource.StartActivity(spanName, kind);

        if (activity is null)
        {
            return (null, meta);
        }

        SetCommonTags(activity, kind, meta);

        if (kind == ActivityKind.Client && _options.InjectOutgoingContext)
        {
            meta = InjectContext(meta, activity);
        }

        return (activity, meta);
    }

    private void SetCommonTags(Activity activity, ActivityKind kind, RequestMeta meta)
    {
        activity.SetTag("rpc.system", _options.RpcSystem);
        activity.SetTag("rpc.service", meta.Service);
        activity.SetTag("rpc.method", meta.Procedure ?? string.Empty);
        activity.SetTag("rpc.transport", meta.Transport ?? "unknown");
        activity.SetTag("rpc.encoding", meta.Encoding ?? "unknown");
        activity.SetTag("rpc.kind", kind == ActivityKind.Server ? "server" : "client");

        if (!string.IsNullOrEmpty(meta.Caller))
        {
            activity.SetTag("rpc.caller", meta.Caller);
        }

        if (!string.IsNullOrEmpty(meta.ShardKey))
        {
            activity.SetTag("rpc.shard_key", meta.ShardKey);
        }

        if (!string.IsNullOrEmpty(meta.RoutingKey))
        {
            activity.SetTag("rpc.routing_key", meta.RoutingKey);
        }

        if (!string.IsNullOrEmpty(meta.RoutingDelegate))
        {
            activity.SetTag("rpc.routing_delegate", meta.RoutingDelegate);
        }
    }

    private static bool TryExtractContext(RequestMeta meta, out ActivityContext context)
    {
        context = default;

        if (!meta.TryGetHeader("traceparent", out var traceParentValue) || string.IsNullOrEmpty(traceParentValue))
        {
            return false;
        }

        meta.TryGetHeader("tracestate", out var traceStateValue);
        return ActivityContext.TryParse(traceParentValue!, traceStateValue, out context);
    }

    private static RequestMeta InjectContext(RequestMeta meta, Activity activity)
    {
        var id = activity.Id;
        if (string.IsNullOrEmpty(id))
        {
            return meta;
        }

        var updated = meta.WithHeader("traceparent", id);

        if (!string.IsNullOrEmpty(activity.TraceStateString))
        {
            updated = updated.WithHeader("tracestate", activity.TraceStateString);
        }

        return updated;
    }

    private static void SetSuccess(Activity activity, string? responseEncoding)
    {
        activity.SetStatus(ActivityStatusCode.Ok);
        if (!string.IsNullOrEmpty(responseEncoding))
        {
            activity.SetTag("rpc.response.encoding", responseEncoding);
        }
    }

    private static void RecordError(Activity activity, Error error)
    {
        activity.SetStatus(ActivityStatusCode.Error, error.Message);
        if (!string.IsNullOrEmpty(error.Code))
        {
            activity.SetTag("rpc.error_code", error.Code);
        }

        if (!string.IsNullOrEmpty(error.Message))
        {
            activity.SetTag("rpc.error_message", error.Message);
        }

        var tags = new ActivityTagsCollection
        {
            { "error.message", error.Message ?? string.Empty },
            { "error.code", error.Code ?? string.Empty }
        };

        activity.AddEvent(new ActivityEvent("rpc.error", tags: tags));
    }

    private static void RecordException(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        var tags = new ActivityTagsCollection
        {
            { "exception.type", exception.GetType().FullName ?? exception.GetType().Name },
            { "exception.message", exception.Message ?? string.Empty },
            { "exception.stacktrace", exception.StackTrace ?? string.Empty }
        };

        activity.AddEvent(new ActivityEvent("exception", tags: tags));
    }

    private sealed class TracingStreamCall(IStreamCall inner, Activity activity) : IStreamCall
    {
        private readonly IStreamCall _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        private Activity? _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        private Error? _error;

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
                _error = error;
            }

            try
            {
                await _inner.CompleteAsync(error, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordException(_activity, ex);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                CloseActivity();
            }
        }

        private void CloseActivity()
        {
            var activity = Interlocked.Exchange(ref _activity, null);
            if (activity is null)
            {
                return;
            }

            if (_error is null)
            {
                SetSuccess(activity, _inner.ResponseMeta.Encoding);
            }
            else
            {
                RecordError(activity, _error);
            }

            activity.Stop();
        }
    }

    private sealed class TracingClientStreamTransportCall : IClientStreamTransportCall
    {
        private readonly IClientStreamTransportCall _inner;
        private readonly RpcTracingMiddleware _owner;
        private Activity? _activity;
        private readonly Lazy<Task<Result<Response<ReadOnlyMemory<byte>>>>> _response;

        public TracingClientStreamTransportCall(IClientStreamTransportCall inner, Activity activity, RpcTracingMiddleware owner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _activity = activity ?? throw new ArgumentNullException(nameof(activity));
            _owner = owner;
            _response = new Lazy<Task<Result<Response<ReadOnlyMemory<byte>>>>>(() => MonitorResponseAsync(inner.Response));
        }

        public RequestMeta RequestMeta => _inner.RequestMeta;

        public ResponseMeta ResponseMeta => _inner.ResponseMeta;

        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> Response => new(_response.Value);

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
                CloseActivity();
            }
        }

        private Task<Result<Response<ReadOnlyMemory<byte>>>> MonitorResponseAsync(ValueTask<Result<Response<ReadOnlyMemory<byte>>>> responseTask)
        {
            if (responseTask.IsCompletedSuccessfully)
            {
                try
                {
                    var result = responseTask.GetAwaiter().GetResult();
                    HandleResponseResult(result);
                    return Task.FromResult(result);
                }
                catch (Exception ex)
                {
                    HandleResponseException(ex);
                    return Task.FromException<Result<Response<ReadOnlyMemory<byte>>>>(ex);
                }
            }

            return AwaitAndHandleAsync(responseTask);

            async Task<Result<Response<ReadOnlyMemory<byte>>>> AwaitAndHandleAsync(ValueTask<Result<Response<ReadOnlyMemory<byte>>>> pending)
            {
                try
                {
                    var result = await pending.ConfigureAwait(false);
                    HandleResponseResult(result);
                    return result;
                }
                catch (Exception ex)
                {
                    HandleResponseException(ex);
                    throw;
                }
            }
        }

        private void HandleResponseResult(Result<Response<ReadOnlyMemory<byte>>> result)
        {
            var activity = _activity;
            if (activity is null)
            {
                return;
            }

            if (result.IsSuccess)
            {
                SetSuccess(activity, result.Value.Meta.Encoding);
            }
            else
            {
                RecordError(activity, result.Error!);
            }
        }

        private void HandleResponseException(Exception exception)
        {
            if (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
            {
                RecordException(_activity, aggregate.InnerExceptions[0]);
            }
            else
            {
                RecordException(_activity, exception);
            }
        }

    private void CloseActivity()
    {
        var activity = Interlocked.Exchange(ref _activity, null);
        activity?.Stop();
    }
}

    private static T EnsureNotNull<T>(T? value, string paramName) where T : class
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        return value;
    }

    private sealed class TracingDuplexStreamCall(IDuplexStreamCall inner, Activity activity) : IDuplexStreamCall
    {
        private readonly IDuplexStreamCall _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        private Activity? _activity = activity ?? throw new ArgumentNullException(nameof(activity));
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

            try
            {
                await _inner.CompleteRequestsAsync(error, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordException(_activity, ex);
                throw;
            }
        }

        public async ValueTask CompleteResponsesAsync(Error? error = null, CancellationToken cancellationToken = default)
        {
            if (error is not null)
            {
                _responseError = error;
            }

            try
            {
                await _inner.CompleteResponsesAsync(error, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RecordException(_activity, ex);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                CloseActivity();
            }
        }

        private void CloseActivity()
        {
            var activity = Interlocked.Exchange(ref _activity, null);
            if (activity is null)
            {
                return;
            }

            var error = _responseError ?? _requestError;
            if (error is null)
            {
                SetSuccess(activity, _inner.ResponseMeta.Encoding);
            }
            else
            {
                RecordError(activity, error);
            }

            activity.Stop();
        }
    }
}

