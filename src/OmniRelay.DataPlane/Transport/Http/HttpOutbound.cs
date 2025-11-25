using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text.Json;
using Hugo;
using Microsoft.AspNetCore.Http;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using OmniRelay.Transport.Http.Middleware;
using static Hugo.Go;

namespace OmniRelay.Transport.Http;

/// <summary>
/// HTTP outbound transport that issues unary and oneway RPC requests over HTTP/1.1, HTTP/2, or HTTP/3.
/// Applies per-call middleware and honors <see cref="HttpClientRuntimeOptions"/> for protocol negotiation.
/// </summary>
public sealed class HttpOutbound : IUnaryOutbound, IOnewayOutbound, IOutboundDiagnostic, IHttpOutboundMiddlewareSink, IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Uri _requestUri;
    private readonly bool _disposeClient;
    private readonly HttpClientRuntimeOptions? _runtimeOptions;
    private HttpOutboundMiddlewareRegistry? _middlewareRegistry;
    private string? _middlewareService;
    private int _middlewareConfigured;
    private ConcurrentDictionary<string, HttpClientMiddlewareHandler>? _middlewarePipelines;
    private TaskQueue<Func<CancellationToken, ValueTask<Result<OnewayAck>>>>? _onewayQueue;
    private SafeTaskQueueWrapper<Func<CancellationToken, ValueTask<Result<OnewayAck>>>>? _onewaySafeQueue;
    private TaskQueueChannelAdapter<Func<CancellationToken, ValueTask<Result<OnewayAck>>>>? _onewayAdapter;
    private CancellationTokenSource? _onewayCts;
    private Task? _onewayPump;
    private int _onewayInitialized;

    /// <summary>
    /// Creates a new HTTP outbound transport targeting a specific endpoint.
    /// </summary>
    /// <param name="httpClient">The HTTP client used to send requests.</param>
    /// <param name="requestUri">The target URI for outbound RPC requests.</param>
    /// <param name="disposeClient">Whether to dispose the provided client when the transport stops.</param>
    /// <param name="runtimeOptions">Optional per-request protocol negotiation and version policy settings.</param>
    [Obsolete("Use HttpOutbound.Create or TryCreate to receive result-based validation and avoid exception flow.")]
    public HttpOutbound(HttpClient httpClient, Uri requestUri, bool disposeClient = false, HttpClientRuntimeOptions? runtimeOptions = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _requestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
        _disposeClient = disposeClient;
        _runtimeOptions = runtimeOptions;
    }

    /// <summary>
    /// Tries to create an HTTP outbound transport without throwing on validation errors.
    /// </summary>
    public static Result<HttpOutbound> TryCreate(
        HttpClient httpClient,
        Uri requestUri,
        bool disposeClient = false,
        HttpClientRuntimeOptions? runtimeOptions = null)
    {
        if (httpClient is null)
        {
            return Err<HttpOutbound>(
                Error.From("HttpClient is required for HttpOutbound.", "http.outbound.argument_missing")
                    .WithMetadata("argument", nameof(httpClient)));
        }

        if (requestUri is null)
        {
            return Err<HttpOutbound>(
                Error.From("Request URI is required for HttpOutbound.", "http.outbound.argument_missing")
                    .WithMetadata("argument", nameof(requestUri)));
        }

        if (runtimeOptions?.EnableHttp3 == true &&
            !requestUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Err<HttpOutbound>(
                Error.From(
                    "HTTP/3 requests require HTTPS endpoints. Update the request URI or disable HTTP/3 for this outbound.",
                    "http.outbound.h3_requires_https")
                    .WithMetadata("uri", requestUri.ToString()));
        }

        // Factory intentionally uses the obsolete constructor to centralize creation; suppress warning locally.
#pragma warning disable CS0618
        return Ok(new HttpOutbound(httpClient, requestUri, disposeClient, runtimeOptions));
#pragma warning restore CS0618
    }

    /// <summary>
    /// Preferred factory for creating an outbound using result pipelines.
    /// </summary>
    public static Result<HttpOutbound> Create(
        HttpClient httpClient,
        Uri requestUri,
        bool disposeClient = false,
        HttpClientRuntimeOptions? runtimeOptions = null) =>
        TryCreate(httpClient, requestUri, disposeClient, runtimeOptions);

    /// <summary>
    /// Starts the outbound transport. No-op for the HTTP client implementation.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        EnsureOnewayQueueInitialized();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Stops the outbound transport, optionally disposing the underlying <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (_onewayCts is not null)
        {
            try
            {
                _onewayCts.Cancel();
            }
            catch
            {
                // best effort
            }
        }

        if (_onewayPump is not null)
        {
            try
            {
                await _onewayPump.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            _onewayPump = null;
        }

        if (_onewayAdapter is not null)
        {
            await _onewayAdapter.DisposeAsync().ConfigureAwait(false);
        }

        if (_onewaySafeQueue is not null)
        {
            await _onewaySafeQueue.DisposeAsync().ConfigureAwait(false);
        }
        _onewayQueue = null;
        _onewaySafeQueue = null;
        _onewayAdapter = null;
        _onewayCts?.Dispose();
        _onewayCts = null;

        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// Performs a unary RPC over HTTP.
    /// </summary>
    /// <param name="request">The request containing metadata and payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The decoded response or an error.</returns>
    private ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallUnaryAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default) =>
        ExecuteHttpCallAsync(
            request,
            HttpOutboundCallKind.Unary,
            HttpCompletionOption.ResponseHeadersRead,
            (httpRequest, response, token) => HandleUnaryResponseAsync(httpRequest, response, request.Meta, token),
            cancellationToken);

    /// <summary>
    /// Performs a oneway RPC over HTTP.
    /// </summary>
    /// <param name="request">The request containing metadata and payload.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An acknowledgement if the server accepted the request; otherwise an error.</returns>
    private ValueTask<Result<OnewayAck>> CallOnewayAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default)
    {
        EnsureOnewayQueueInitialized();

        var tcs = new TaskCompletionSource<Result<OnewayAck>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var work = new Func<CancellationToken, ValueTask<Result<OnewayAck>>>(async ct =>
        {
            var result = await ExecuteHttpCallAsync(
                    request,
                    HttpOutboundCallKind.Oneway,
                    HttpCompletionOption.ResponseContentRead,
                    (httpRequest, response, token) => HandleOnewayResponseAsync(httpRequest, response, request.Meta, token),
                    ct)
                .ConfigureAwait(false);

            tcs.TrySetResult(result);
            return result;
        });

        var enqueue = _onewaySafeQueue!.EnqueueAsync(work, cancellationToken);
        return AwaitEnqueuedAsync(enqueue, tcs, cancellationToken);
    }

    /// <summary>
    /// Builds an <see cref="HttpRequestMessage"/> from the RPC request metadata and body.
    /// </summary>
    /// <param name="request">The RPC request.</param>
    /// <returns>A configured HTTP request message.</returns>
    private HttpRequestMessage BuildHttpRequest(IRequest<ReadOnlyMemory<byte>> request)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, _requestUri);
        var content = new ReadOnlyMemoryContent(request.Body);

        ApplyHttpClientRuntimeOptions(httpRequest);

        var encoding = request.Meta.Encoding;
        var contentType = ResolveContentType(encoding);

        if (!string.IsNullOrEmpty(contentType))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        httpRequest.Content = content;

        httpRequest.Headers.Add(HttpTransportHeaders.Transport, "http");
        httpRequest.Headers.Add(HttpTransportHeaders.Procedure, request.Meta.Procedure ?? string.Empty);

        if (!string.IsNullOrEmpty(request.Meta.Caller))
        {
            httpRequest.Headers.Add(HttpTransportHeaders.Caller, request.Meta.Caller);
        }

        if (!string.IsNullOrEmpty(encoding))
        {
            httpRequest.Headers.Add(HttpTransportHeaders.Encoding, encoding);
        }

        if (!string.IsNullOrEmpty(request.Meta.ShardKey))
        {
            httpRequest.Headers.Add(HttpTransportHeaders.ShardKey, request.Meta.ShardKey);
        }

        if (!string.IsNullOrEmpty(request.Meta.RoutingKey))
        {
            httpRequest.Headers.Add(HttpTransportHeaders.RoutingKey, request.Meta.RoutingKey);
        }

        if (!string.IsNullOrEmpty(request.Meta.RoutingDelegate))
        {
            httpRequest.Headers.Add(HttpTransportHeaders.RoutingDelegate, request.Meta.RoutingDelegate);
        }

        if (request.Meta.TimeToLive is { } ttl)
        {
            httpRequest.Headers.Add(HttpTransportHeaders.TtlMs, ttl.TotalMilliseconds.ToString(CultureInfo.InvariantCulture));
        }

        if (request.Meta.Deadline is { } deadline)
        {
            httpRequest.Headers.Add(HttpTransportHeaders.Deadline, deadline.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        }

        foreach (var header in request.Meta.Headers)
        {
            if (!httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                httpRequest.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return httpRequest;
    }

    private void EnsureOnewayQueueInitialized()
    {
        if (Interlocked.CompareExchange(ref _onewayInitialized, 1, 0) != 0 && _onewaySafeQueue is not null)
        {
            return;
        }

        var options = new TaskQueueOptions
        {
            Capacity = 64,
            LeaseDuration = TimeSpan.FromSeconds(30),
            HeartbeatInterval = TimeSpan.FromSeconds(5),
            LeaseSweepInterval = TimeSpan.FromSeconds(10),
            RequeueDelay = TimeSpan.FromMilliseconds(50),
            MaxDeliveryAttempts = 1,
            Name = "http-oneway-send"
        };

        var queue = new TaskQueue<Func<CancellationToken, ValueTask<Result<OnewayAck>>>>(options, TimeProvider.System, (_, _) => ValueTask.CompletedTask);
        var safe = new SafeTaskQueueWrapper<Func<CancellationToken, ValueTask<Result<OnewayAck>>>>(queue, ownsQueue: true);
        var adapter = TaskQueueChannelAdapter<Func<CancellationToken, ValueTask<Result<OnewayAck>>>>.Create(queue, concurrency: 4, ownsQueue: false);

        _onewayQueue = queue;
        _onewaySafeQueue = safe;
        _onewayAdapter = adapter;
        _onewayCts = new CancellationTokenSource();
        _onewayPump = RunOnewayPumpAsync(adapter, safe, _onewayCts.Token);
    }

    private static async Task RunOnewayPumpAsync(
        TaskQueueChannelAdapter<Func<CancellationToken, ValueTask<Result<OnewayAck>>>> adapter,
        SafeTaskQueueWrapper<Func<CancellationToken, ValueTask<Result<OnewayAck>>>> safeQueue,
        CancellationToken cancellationToken)
    {
        await foreach (var lease in adapter.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var safeLease = safeQueue.Wrap(lease);
            Result<OnewayAck> result;
            try
            {
                result = await lease.Value(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken || cancellationToken.IsCancellationRequested)
            {
                result = Err<OnewayAck>(Error.Canceled(token: cancellationToken));
            }
            catch (Exception ex)
            {
                result = Err<OnewayAck>(Error.FromException(ex));
            }

            if (result.IsSuccess)
            {
                await safeLease.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await safeLease.FailAsync(result.Error!, requeue: false, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask<Result<OnewayAck>> AwaitEnqueuedAsync(
        ValueTask<Result<Unit>> enqueueTask,
        TaskCompletionSource<Result<OnewayAck>> tcs,
        CancellationToken cancellationToken)
    {
        var enqueue = await enqueueTask.ConfigureAwait(false);
        if (enqueue.IsFailure)
        {
            return Err<OnewayAck>(enqueue.Error);
        }

        using var registration = cancellationToken.Register(() =>
        {
            tcs.TrySetCanceled(cancellationToken);
        });

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Err<OnewayAck>(Error.Canceled(token: cancellationToken));
        }
    }

    /// <summary>
    /// Applies client runtime options to the outgoing HTTP request for version and policy negotiation.
    /// </summary>
    /// <param name="httpRequest">The HTTP request to configure.</param>
    private void ApplyHttpClientRuntimeOptions(HttpRequestMessage httpRequest)
    {
        if (_runtimeOptions is null)
        {
            return;
        }

        if (_runtimeOptions.EnableHttp3)
        {
            var http3Policy = _runtimeOptions.VersionPolicy ?? HttpVersionPolicy.RequestVersionOrLower;
            httpRequest.VersionPolicy = http3Policy;

            if (_runtimeOptions.RequestVersion is { } desiredVersion)
            {
                httpRequest.Version = desiredVersion;
            }
            else
            {
                httpRequest.Version = HttpVersion.Version30;
            }

            return;
        }

        if (_runtimeOptions.RequestVersion is { } version)
        {
            httpRequest.Version = version;
        }

        if (_runtimeOptions.VersionPolicy is { } versionPolicy)
        {
            httpRequest.VersionPolicy = versionPolicy;
        }
    }

    /// <summary>
    /// Resolves the content type header value from an RPC encoding.
    /// </summary>
    /// <param name="encoding">The RPC encoding (e.g., json, raw, protobuf).</param>
    /// <returns>A media type string or <see langword="null"/> when unknown.</returns>
    private static string? ResolveContentType(string? encoding)
    {
        if (string.IsNullOrEmpty(encoding))
        {
            return null;
        }

        if (string.Equals(encoding, RawCodec.DefaultEncoding, StringComparison.OrdinalIgnoreCase))
        {
            return MediaTypeNames.Application.Octet;
        }

        var mediaType = ProtobufEncoding.GetMediaType(encoding);
        if (!string.IsNullOrEmpty(mediaType))
        {
            return mediaType;
        }

        return encoding;
    }

    /// <summary>
    /// Constructs <see cref="ResponseMeta"/> from HTTP response headers.
    /// </summary>
    /// <param name="response">The HTTP response message.</param>
    /// <returns>Response metadata including encoding, transport, and headers.</returns>
    private static ResponseMeta BuildResponseMeta(HttpResponseMessage response)
    {
        var headers = new List<KeyValuePair<string, string>>();

        foreach (var header in response.Headers)
        {
            headers.Add(new KeyValuePair<string, string>(header.Key, string.Join(",", header.Value)));
        }

        foreach (var header in response.Content.Headers)
        {
            headers.Add(new KeyValuePair<string, string>(header.Key, string.Join(",", header.Value)));
        }

        if (!headers.Any(static header => string.Equals(header.Key, HttpTransportHeaders.Protocol, StringComparison.OrdinalIgnoreCase)))
        {
            headers.Add(new KeyValuePair<string, string>(HttpTransportHeaders.Protocol, FormatProtocol(response.Version)));
        }

        response.Headers.TryGetValues(HttpTransportHeaders.Encoding, out var encodingValues);
        var encoding = encodingValues?.FirstOrDefault() ?? response.Content.Headers.ContentType?.MediaType;
        encoding = ProtobufEncoding.Normalize(encoding);

        return new ResponseMeta(
            encoding: encoding,
            transport: "http",
            headers: headers);
    }

    /// <summary>
    /// Formats a System.Version to an HTTP protocol label (HTTP/1.0, HTTP/1.1, HTTP/2, HTTP/3).
    /// </summary>
    /// <param name="version">The HTTP version.</param>
    /// <returns>The formatted protocol label.</returns>
    private static string FormatProtocol(Version version)
    {
        if (version is null)
        {
            return "HTTP/1.1";
        }

        if (version.Major >= 3)
        {
            return "HTTP/3";
        }

        if (version.Major == 2)
        {
            return "HTTP/2";
        }

        if (version.Major == 1)
        {
            return version.Minor <= 0 ? "HTTP/1.0" : "HTTP/1.1";
        }

        return $"HTTP/{version}";
    }

    /// <summary>
    /// Attempts to parse a structured OmniRelay error from an HTTP response; falls back to status mapping.
    /// </summary>
    /// <param name="response">The HTTP response.</param>
    /// <param name="transport">The transport name used for error attribution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="cachedPayload">Optional cached payload to avoid re-reading the response stream.</param>
    /// <returns>A normalized error.</returns>
    private static async ValueTask<Error> ReadErrorAsync(
        HttpResponseMessage response,
        string transport,
        CancellationToken cancellationToken,
        byte[]? cachedPayload = null)
    {
        try
        {
            var payload = cachedPayload ?? await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (payload.Length > 0)
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.TryGetProperty("message", out var messageProperty))
                {
                    var message = messageProperty.GetString() ?? response.ReasonPhrase ?? "HTTP error";
                    string? code = null;
                    if (document.RootElement.TryGetProperty("code", out var codeProperty))
                    {
                        code = codeProperty.GetString();
                    }

                    var status = document.RootElement.TryGetProperty("status", out var statusProperty) &&
                                 Enum.TryParse(statusProperty.GetString(), out OmniRelayStatusCode parsedStatus)
                        ? parsedStatus
                        : HttpStatusMapper.FromStatusCode((int)response.StatusCode);

                    var error = OmniRelayErrorAdapter.FromStatus(status, message, transport: transport);
                    if (!string.IsNullOrEmpty(code))
                    {
                        error = error.WithCode(code);
                    }

                    return error;
                }
            }
        }
        catch (JsonException)
        {
            // Fall back to status mapping below
        }

        var fallbackStatus = HttpStatusMapper.FromStatusCode((int)response.StatusCode);
        var fallbackMessage = response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}";
        return OmniRelayErrorAdapter.FromStatus(fallbackStatus, fallbackMessage, transport: transport);
    }

    private ValueTask<Result<T>> ExecuteHttpCallAsync<T>(
        IRequest<ReadOnlyMemory<byte>> request,
        HttpOutboundCallKind callKind,
        HttpCompletionOption completionOption,
        Func<HttpRequestMessage, HttpResponseMessage, CancellationToken, ValueTask<Result<T>>> handler,
        CancellationToken cancellationToken)
    {
        return Go.Ok(request)
            .Ensure(
                req => !string.IsNullOrWhiteSpace(req.Meta.Procedure),
                req => BuildMissingProcedureError(req.Meta))
            .ThenAsync(
                (validatedRequest, token) => RunHttpCallAsync(validatedRequest, callKind, completionOption, handler, token),
                cancellationToken);
    }

    private ValueTask<Result<T>> RunHttpCallAsync<T>(
        IRequest<ReadOnlyMemory<byte>> request,
        HttpOutboundCallKind callKind,
        HttpCompletionOption completionOption,
        Func<HttpRequestMessage, HttpResponseMessage, CancellationToken, ValueTask<Result<T>>> handler,
        CancellationToken cancellationToken)
    {
        async ValueTask<Result<T>> InvokeAsync(CancellationToken token)
        {
            try
            {
                using var httpRequest = BuildHttpRequest(request);
                using var response = await SendWithMiddlewareAsync(
                        httpRequest,
                        request.Meta,
                        callKind,
                        completionOption,
                        token)
                    .ConfigureAwait(false);

                return await handler(httpRequest, response, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Err<T>(NormalizeHttpOutboundException(ex));
            }
        }

        var timeout = ResolveRequestTimeout(request.Meta);

        if (timeout is { } remaining)
        {
            if (remaining <= TimeSpan.Zero)
            {
                return new ValueTask<Result<T>>(Go.Err<T>(BuildTimeoutError(request.Meta)));
            }

            return Go.WithTimeoutValueTaskAsync(
                InvokeAsync,
                remaining,
                cancellationToken: cancellationToken);
        }

        return InvokeAsync(cancellationToken);
    }

    private async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> HandleUnaryResponseAsync(
        HttpRequestMessage httpRequest,
        HttpResponseMessage response,
        RequestMeta requestMeta,
        CancellationToken cancellationToken)
    {
        var responseMeta = BuildResponseMeta(response);
        RecordHttp3Fallback(response, responseMeta, httpRequest, requestMeta);

        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return Ok(Response<ReadOnlyMemory<byte>>.Create(payload, responseMeta));
        }

        var error = await ReadErrorAsync(response, "http", cancellationToken, payload).ConfigureAwait(false);
        return Err<Response<ReadOnlyMemory<byte>>>(error);
    }

    private async ValueTask<Result<OnewayAck>> HandleOnewayResponseAsync(
        HttpRequestMessage httpRequest,
        HttpResponseMessage response,
        RequestMeta requestMeta,
        CancellationToken cancellationToken)
    {
        var responseMeta = BuildResponseMeta(response);
        RecordHttp3Fallback(response, responseMeta, httpRequest, requestMeta);

        if (response.StatusCode == HttpStatusCode.Accepted ||
            response.StatusCode == (HttpStatusCode)StatusCodes.Status202Accepted)
        {
            return Ok(OnewayAck.Ack(responseMeta));
        }

        var error = await ReadErrorAsync(response, "http", cancellationToken).ConfigureAwait(false);
        return Err<OnewayAck>(error);
    }

    private void RecordHttp3Fallback(
        HttpResponseMessage response,
        ResponseMeta responseMeta,
        HttpRequestMessage httpRequest,
        RequestMeta requestMeta)
    {
        if (_runtimeOptions?.EnableHttp3 != true)
        {
            return;
        }

        var observed = responseMeta.Headers.FirstOrDefault(
            static header => string.Equals(header.Key, HttpTransportHeaders.Protocol, StringComparison.OrdinalIgnoreCase)).Value
                       ?? FormatProtocol(response.Version);

        if (string.IsNullOrWhiteSpace(observed) ||
            observed.StartsWith("HTTP/3", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var baseTags = HttpTransportMetrics.CreateBaseTags(
            requestMeta.Service ?? string.Empty,
            requestMeta.Procedure ?? string.Empty,
            httpRequest.Method.Method,
            observed);

        var tags = HttpTransportMetrics.AppendObservedProtocol(baseTags, observed);
        HttpTransportMetrics.ClientProtocolFallbacks.Add(1, tags);
    }

    private static Error NormalizeHttpOutboundException(Exception exception)
    {
        if (exception is ResultException resultException && resultException.Error is not null)
        {
            return resultException.Error;
        }

        return OmniRelayErrors.FromException(exception, transport: "http").Error;
    }

    private static TimeSpan? ResolveRequestTimeout(RequestMeta meta, TimeProvider? timeProvider = null)
    {
        var ttlTimeout = NormalizeTimeout(meta.TimeToLive);
        TimeSpan? deadlineTimeout = null;

        if (meta.Deadline is { } deadline)
        {
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
            deadlineTimeout = NormalizeTimeout(deadline - now);
        }

        if (ttlTimeout is null)
        {
            return deadlineTimeout;
        }

        if (deadlineTimeout is null)
        {
            return ttlTimeout;
        }

        return ttlTimeout.Value <= deadlineTimeout.Value ? ttlTimeout : deadlineTimeout;
    }

    private static TimeSpan? NormalizeTimeout(TimeSpan? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Value <= TimeSpan.Zero ? TimeSpan.Zero : value;
    }

    private static TimeSpan? NormalizeTimeout(TimeSpan value) =>
        value <= TimeSpan.Zero ? TimeSpan.Zero : value;

    private static Error BuildTimeoutError(RequestMeta meta) =>
        Error.Timeout(message: "The HTTP request deadline elapsed before sending.")
            .WithMetadata("service", meta.Service)
            .WithMetadata("procedure", meta.Procedure ?? string.Empty);

    private static Error BuildMissingProcedureError(RequestMeta meta) =>
        OmniRelayErrorAdapter.FromStatus(
            OmniRelayStatusCode.InvalidArgument,
            "Procedure metadata is required for HTTP outbound calls.",
            transport: "http")
        .WithMetadata("service", meta.Service);

    /// <inheritdoc />
    async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> IUnaryOutbound.CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken) =>
        await CallUnaryAsync(request, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    async ValueTask<Result<OnewayAck>> IOnewayOutbound.CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken) =>
        await CallOnewayAsync(request, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Returns a snapshot of the outbound transport configuration for diagnostics.
    /// </summary>
    public object GetOutboundDiagnostics() =>
        new HttpOutboundSnapshot(_requestUri, _disposeClient);

    /// <summary>
    /// Attaches the middleware registry for the specified service, enabling per-procedure pipelines.
    /// </summary>
    /// <param name="service">The service name.</param>
    /// <param name="registry">The registry of HTTP client middleware.</param>
    void IHttpOutboundMiddlewareSink.Attach(string service, HttpOutboundMiddlewareRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (Interlocked.Exchange(ref _middlewareConfigured, 1) == 1)
        {
            return;
        }

        _middlewareService = string.IsNullOrWhiteSpace(service) ? string.Empty : service;
        _middlewareRegistry = registry;
        _middlewarePipelines = new ConcurrentDictionary<string, HttpClientMiddlewareHandler>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sends the HTTP request through the configured middleware pipeline.
    /// </summary>
    /// <param name="httpRequest">The HTTP request.</param>
    /// <param name="requestMeta">The RPC request metadata.</param>
    /// <param name="callKind">The call kind (unary or oneway).</param>
    /// <param name="completionOption">The HTTP completion option.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The HTTP response message.</returns>
    private ValueTask<HttpResponseMessage> SendWithMiddlewareAsync(
        HttpRequestMessage httpRequest,
        RequestMeta requestMeta,
        HttpOutboundCallKind callKind,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        var registry = _middlewareRegistry;
        var service = _middlewareService;

        if (registry is null || string.IsNullOrEmpty(service))
        {
            return new ValueTask<HttpResponseMessage>(_httpClient.SendAsync(httpRequest, completionOption, cancellationToken));
        }

        var middleware = registry.Resolve(service, requestMeta.Procedure);

        if (middleware.Count == 0)
        {
            return new ValueTask<HttpResponseMessage>(_httpClient.SendAsync(httpRequest, completionOption, cancellationToken));
        }

        var cacheKey = BuildCacheKey(callKind, requestMeta.Procedure);
        var pipelines = _middlewarePipelines;
        HttpClientMiddlewareHandler pipeline;

        if (pipelines is not null)
        {
            pipeline = pipelines.GetOrAdd(cacheKey, _ => ComposePipeline(middleware));
        }
        else
        {
            pipeline = ComposePipeline(middleware);
        }

        var context = new HttpClientMiddlewareContext(httpRequest, requestMeta, callKind, completionOption);
        return pipeline(context, cancellationToken);

        HttpClientMiddlewareHandler ComposePipeline(IReadOnlyList<IHttpClientMiddleware> source)
        {
            return HttpClientMiddlewareComposer.Compose(source, Terminal);
        }

        ValueTask<HttpResponseMessage> Terminal(HttpClientMiddlewareContext ctx, CancellationToken token) =>
            new(_httpClient.SendAsync(ctx.Request, ctx.CompletionOption, token));
    }

    /// <summary>
    /// Builds a cache key for middleware pipelines by call kind and procedure.
    /// </summary>
    /// <param name="callKind">The call kind.</param>
    /// <param name="procedure">The optional procedure name.</param>
    /// <returns>A stable cache key.</returns>
    private static string BuildCacheKey(HttpOutboundCallKind callKind, string? procedure)
    {
        var normalizedProcedure = string.IsNullOrWhiteSpace(procedure) ? string.Empty : procedure!;
        return callKind switch
        {
            HttpOutboundCallKind.Oneway => $"o:{normalizedProcedure}",
            _ => $"u:{normalizedProcedure}"
        };
    }
}

/// <summary>
/// Snapshot of the HTTP outbound configuration for diagnostics.
/// </summary>
public sealed record HttpOutboundSnapshot(Uri RequestUri, bool DisposesClient)
{
    public Uri RequestUri { get; init; } = RequestUri;

    public bool DisposesClient { get; init; } = DisposesClient;
}
