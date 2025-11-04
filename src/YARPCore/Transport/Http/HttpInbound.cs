using System.Globalization;
using System.Net.Mime;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hugo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using YARPCore.Core;
using YARPCore.Core.Transport;
using YARPCore.Dispatcher;
using YARPCore.Errors;

namespace YARPCore.Transport.Http;

public sealed class HttpInbound : ILifecycle, IDispatcherAware
{
    private readonly string[] _urls;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly Action<WebApplication>? _configureApp;
    private WebApplication? _app;
    private Dispatcher.Dispatcher? _dispatcher;
    private volatile bool _isDraining;
    private int _activeRequests;
    private TaskCompletionSource<bool> _drainCompletion = CreateDrainTcs();
    private static readonly JsonSerializerOptions IntrospectionSerializerOptions = CreateIntrospectionSerializerOptions();
    private static readonly JsonSerializerOptions HealthSerializerOptions = CreateHealthSerializerOptions();
    private const string RetryAfterHeaderValue = "1";

    public HttpInbound(
        IEnumerable<string> urls,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null)
    {
        ArgumentNullException.ThrowIfNull(urls);

        _urls = [.. urls];
        if (_urls.Length == 0)
        {
            throw new ArgumentException("At least one URL must be provided for the HTTP inbound.", nameof(urls));
        }

        _configureServices = configureServices;
        _configureApp = configureApp;
    }

    public IReadOnlyCollection<string> Urls =>
        _app?.Urls as IReadOnlyCollection<string> ?? [];

    public void Bind(Dispatcher.Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        if (_dispatcher is null)
        {
            throw new InvalidOperationException("Dispatcher must be bound before starting the HTTP inbound.");
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls(_urls);

        builder.Services.AddRouting();
        _configureServices?.Invoke(builder.Services);
        builder.Services.AddSingleton(_dispatcher);

        var app = builder.Build();

        app.UseWebSockets();

        _configureApp?.Invoke(app);

        app.MapGet("/polymer/introspect", HandleIntrospectAsync);
        app.MapGet("/healthz", HandleHealthzAsync);
        app.MapGet("/readyz", HandleReadyzAsync);
        app.MapMethods("/{**_}", [HttpMethods.Post], HandleUnaryAsync);
        app.MapMethods("/{**_}", [HttpMethods.Get], HandleServerStreamAsync);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null)
        {
            return;
        }

        _isDraining = true;

        if (Volatile.Read(ref _activeRequests) == 0)
        {
            _drainCompletion.TrySetResult(true);
        }

        try
        {
            await _drainCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // fall through for fast shutdown
        }

        try
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignore cancellation during shutdown to force stop
        }

        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
        _isDraining = false;
        Interlocked.Exchange(ref _activeRequests, 0);
        _drainCompletion = CreateDrainTcs();
    }

    private static JsonSerializerOptions CreateIntrospectionSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static JsonSerializerOptions CreateHealthSerializerOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

    private static TaskCompletionSource<bool> CreateDrainTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool TryBeginRequest(HttpContext context)
    {
        if (_isDraining)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers["Retry-After"] = RetryAfterHeaderValue;
            return false;
        }

        Interlocked.Increment(ref _activeRequests);
        return true;
    }

    private void CompleteRequest()
    {
        if (Interlocked.Decrement(ref _activeRequests) == 0 && _isDraining)
        {
            _drainCompletion.TrySetResult(true);
        }
    }

    private async Task HandleIntrospectAsync(HttpContext context)
    {
        if (_dispatcher is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.CompleteAsync().ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = MediaTypeNames.Application.Json;

        var introspection = _dispatcher.Introspect();
        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                introspection,
                IntrospectionSerializerOptions,
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    private Task HandleHealthzAsync(HttpContext context) =>
        HandleHealthAsync(context, readiness: false);

    private Task HandleReadyzAsync(HttpContext context) =>
        HandleHealthAsync(context, readiness: true);

    private async Task HandleHealthAsync(HttpContext context, bool readiness)
    {
        var dispatcher = _dispatcher;
        var issues = new List<string>();
        var healthy = dispatcher is not null && _app is not null;

        if (!healthy)
        {
            if (dispatcher is null)
            {
                issues.Add("dispatcher:unbound");
            }

            if (_app is null)
            {
                issues.Add("http-inbound:not-started");
            }
        }

        if (healthy && readiness)
        {
            var readinessResult = DispatcherHealthEvaluator.Evaluate(dispatcher!);
            if (!readinessResult.IsReady)
            {
                issues.AddRange(readinessResult.Issues);
            }

            if (_isDraining)
            {
                issues.Add("http-inbound:draining");
            }

            healthy = readinessResult.IsReady && !_isDraining;
        }

        context.Response.StatusCode = healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = MediaTypeNames.Application.Json;

        var payload = new
        {
            status = healthy ? "ok" : "unavailable",
            mode = readiness ? "ready" : "live",
            issues = issues.Count == 0 ? [] : issues.ToArray(),
            activeRequests = Math.Max(Volatile.Read(ref _activeRequests), 0),
            draining = _isDraining
        };

        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                payload,
                HealthSerializerOptions,
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    private async Task HandleUnaryAsync(HttpContext context)
    {
        var dispatcher = _dispatcher!;
        var transport = "http";

        if (!TryBeginRequest(context))
        {
            return;
        }

        try
        {
            if (!context.Request.Headers.TryGetValue(HttpTransportHeaders.Procedure, out var procedureValues) ||
            StringValues.IsNullOrEmpty(procedureValues))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteErrorAsync(context, "rpc procedure header missing", PolymerStatusCode.InvalidArgument, transport).ConfigureAwait(false);
                return;
            }

            var procedure = procedureValues![0];

            var encoding = ResolveRequestEncoding(context.Request.Headers, context.Request.ContentType);

            var meta = BuildRequestMeta(dispatcher.ServiceName, procedure!, encoding, context.Request.Headers, transport, context.RequestAborted);

            byte[] buffer;
            if (context.Request.ContentLength is > 0)
            {
                buffer = new byte[context.Request.ContentLength.Value];
                await context.Request.Body.ReadExactlyAsync(buffer.AsMemory(), context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                using var memory = new MemoryStream();
                await context.Request.Body.CopyToAsync(memory, context.RequestAborted).ConfigureAwait(false);
                buffer = memory.ToArray();
            }

            var request = new Request<ReadOnlyMemory<byte>>(meta, buffer);

            if (dispatcher.TryGetProcedure(procedure!, ProcedureKind.Oneway, out _))
            {
                var onewayResult = await dispatcher.InvokeOnewayAsync(procedure!, request, context.RequestAborted).ConfigureAwait(false);
                if (onewayResult.IsFailure)
                {
                    var error = onewayResult.Error!;
                    var exception = PolymerErrors.FromError(error, transport);
                    await WriteErrorAsync(context, exception.Message, exception.StatusCode, transport, error).ConfigureAwait(false);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status202Accepted;
                context.Response.Headers[HttpTransportHeaders.Transport] = transport;

                var ackMeta = onewayResult.Value.Meta;
                var ackEncoding = ackMeta.Encoding ?? encoding;
                context.Response.Headers[HttpTransportHeaders.Encoding] = ackEncoding ?? MediaTypeNames.Application.Octet;
                context.Response.ContentType = ResolveContentType(ackEncoding) ?? MediaTypeNames.Application.Octet;

                foreach (var header in ackMeta.Headers)
                {
                    context.Response.Headers[header.Key] = header.Value;
                }
                return;
            }

            var result = await dispatcher.InvokeUnaryAsync(procedure!, request, context.RequestAborted).ConfigureAwait(false);

            if (result.IsFailure)
            {
                var error = result.Error!;
                var exception = PolymerErrors.FromError(error, transport);
                await WriteErrorAsync(context, exception.Message, exception.StatusCode, transport, error).ConfigureAwait(false);
                return;
            }

            var response = result.Value;
            context.Response.StatusCode = StatusCodes.Status200OK;
            var responseEncoding = response.Meta.Encoding ?? encoding;
            context.Response.Headers[HttpTransportHeaders.Encoding] = responseEncoding ?? MediaTypeNames.Application.Octet;
            context.Response.Headers[HttpTransportHeaders.Transport] = transport;
            context.Response.ContentType = ResolveContentType(responseEncoding) ?? MediaTypeNames.Application.Octet;

            foreach (var header in response.Meta.Headers)
            {
                context.Response.Headers[header.Key] = header.Value;
            }

            if (!response.Body.IsEmpty)
            {
                await context.Response.BodyWriter.WriteAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
            }
        }
        finally
        {
            CompleteRequest();
        }
    }

    private async Task HandleServerStreamAsync(HttpContext context)
    {
        var dispatcher = _dispatcher!;
        var transport = "http";

        if (context.WebSockets.IsWebSocketRequest)
        {
            await HandleDuplexAsync(context).ConfigureAwait(false);
            return;
        }

        if (!TryBeginRequest(context))
        {
            return;
        }

        try
        {

            if (!HttpMethods.IsGet(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            if (!context.Request.Headers.TryGetValue(HttpTransportHeaders.Procedure, out var procedureValues) ||
                StringValues.IsNullOrEmpty(procedureValues))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteErrorAsync(context, "rpc procedure header missing", PolymerStatusCode.InvalidArgument, transport).ConfigureAwait(false);
                return;
            }

            var procedure = procedureValues![0];

            var acceptValues = context.Request.Headers.TryGetValue("Accept", out var acceptRaw)
                ? acceptRaw
                : StringValues.Empty;

            if (acceptValues.Count == 0 ||
                !acceptValues.Any(static value => !string.IsNullOrEmpty(value) && value.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.StatusCode = StatusCodes.Status406NotAcceptable;
                await WriteErrorAsync(context, "text/event-stream Accept header required for streaming", PolymerStatusCode.InvalidArgument, transport).ConfigureAwait(false);
                return;
            }

            var meta = BuildRequestMeta(
                dispatcher.ServiceName,
                procedure!,
                encoding: ResolveRequestEncoding(context.Request.Headers, context.Request.ContentType),
                headers: context.Request.Headers,
                transport: transport,
                cancellationToken: context.RequestAborted);

            var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

            var streamResult = await dispatcher.InvokeStreamAsync(
                procedure!,
                request,
                new StreamCallOptions(StreamDirection.Server),
                context.RequestAborted).ConfigureAwait(false);

            if (streamResult.IsFailure)
            {
                var error = streamResult.Error!;
                var exception = PolymerErrors.FromError(error, transport);
                context.Response.StatusCode = HttpStatusMapper.ToStatusCode(exception.StatusCode);
                await WriteErrorAsync(context, exception.Message, exception.StatusCode, transport, error).ConfigureAwait(false);
                return;
            }

            await using (streamResult.Value.AsAsyncDisposable(out var call))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.Headers[HttpTransportHeaders.Transport] = transport;
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers.Connection = "keep-alive";
                context.Response.Headers.ContentType = "text/event-stream";

                var responseMeta = call.ResponseMeta ?? new ResponseMeta();
                var responseHeaders = responseMeta.Headers ?? [];
                foreach (var header in responseHeaders)
                {
                    context.Response.Headers[header.Key] = header.Value;
                }

                await context.Response.BodyWriter.FlushAsync(context.RequestAborted).ConfigureAwait(false);

                await foreach (var payload in call.Responses.ReadAllAsync(context.RequestAborted).ConfigureAwait(false))
                {
                    var frame = EncodeSseFrame(payload, responseMeta.Encoding);
                    await context.Response.BodyWriter.WriteAsync(frame, context.RequestAborted).ConfigureAwait(false);
                    await context.Response.BodyWriter.FlushAsync(context.RequestAborted).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            CompleteRequest();
        }
    }

    private async Task HandleDuplexAsync(HttpContext context)
    {
        var dispatcher = _dispatcher!;
        const string transport = "http";

        if (!TryBeginRequest(context))
        {
            return;
        }

        try
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteErrorAsync(context, "WebSocket upgrade required for duplex streaming.", PolymerStatusCode.InvalidArgument, transport).ConfigureAwait(false);
                return;
            }

            if (!context.Request.Headers.TryGetValue(HttpTransportHeaders.Procedure, out var procedureValues) ||
                StringValues.IsNullOrEmpty(procedureValues))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteErrorAsync(context, "rpc procedure header missing", PolymerStatusCode.InvalidArgument, transport).ConfigureAwait(false);
                return;
            }

            var procedure = procedureValues![0];

            var meta = BuildRequestMeta(
                dispatcher.ServiceName,
                procedure!,
                encoding: ResolveRequestEncoding(context.Request.Headers, context.Request.ContentType),
                headers: context.Request.Headers,
                transport: transport,
                cancellationToken: context.RequestAborted);

            var dispatcherRequest = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

            var callResult = await dispatcher.InvokeDuplexAsync(procedure!, dispatcherRequest, context.RequestAborted).ConfigureAwait(false);

            if (callResult.IsFailure)
            {
                var error = callResult.Error!;
                var exception = PolymerErrors.FromError(error, transport);
                context.Response.StatusCode = HttpStatusMapper.ToStatusCode(exception.StatusCode);
                await WriteErrorAsync(context, exception.Message, exception.StatusCode, transport, error).ConfigureAwait(false);
                return;
            }

            var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            context.Response.Headers[HttpTransportHeaders.Transport] = transport;

            var call = callResult.Value;
            var requestBuffer = new byte[32 * 1024];
            var responseBuffer = new byte[32 * 1024];

            try
            {
                var requestPump = PumpRequestsAsync(socket, call, requestBuffer, context.RequestAborted);
                var responsePump = PumpResponsesAsync(socket, call, responseBuffer, context.RequestAborted);

                await Task.WhenAll(requestPump, responsePump).ConfigureAwait(false);
            }
            finally
            {
                await call.DisposeAsync().ConfigureAwait(false);

                if (socket.State == WebSocketState.Open)
                {
                    try
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "completed", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (WebSocketException)
                    {
                        // ignore handshake failures during shutdown
                    }
                }

                socket.Dispose();
            }

            async Task PumpRequestsAsync(WebSocket webSocket, IDuplexStreamCall streamCall, byte[] tempBuffer, CancellationToken cancellationToken)
            {
                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var frame = await HttpDuplexProtocol.ReceiveFrameAsync(webSocket, tempBuffer, cancellationToken).ConfigureAwait(false);

                        if (frame.MessageType == WebSocketMessageType.Close)
                        {
                            await streamCall.CompleteRequestsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                            return;
                        }

                        switch (frame.Type)
                        {
                            case HttpDuplexProtocol.FrameType.RequestData:
                                await streamCall.RequestWriter.WriteAsync(frame.Payload.ToArray(), cancellationToken).ConfigureAwait(false);
                                break;

                            case HttpDuplexProtocol.FrameType.RequestComplete:
                                await streamCall.CompleteRequestsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                                return;

                            case HttpDuplexProtocol.FrameType.RequestError:
                                {
                                    var error = HttpDuplexProtocol.ParseError(frame.Payload.Span, transport);
                                    await streamCall.CompleteRequestsAsync(error, CancellationToken.None).ConfigureAwait(false);
                                    await streamCall.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
                                    return;
                                }

                            case HttpDuplexProtocol.FrameType.ResponseError:
                                {
                                    var error = HttpDuplexProtocol.ParseError(frame.Payload.Span, transport);
                                    await streamCall.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
                                    return;
                                }

                            case HttpDuplexProtocol.FrameType.ResponseComplete:
                                await streamCall.CompleteResponsesAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                                break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    var error = PolymerErrorAdapter.FromStatus(
                        PolymerStatusCode.Cancelled,
                        "The client cancelled the request.",
                        transport: transport);
                    await streamCall.CompleteRequestsAsync(error, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var error = PolymerErrorAdapter.FromStatus(
                        PolymerStatusCode.Internal,
                        ex.Message ?? "An error occurred while reading the duplex request stream.",
                        transport: transport,
                        inner: Error.FromException(ex));
                    await streamCall.CompleteRequestsAsync(error, CancellationToken.None).ConfigureAwait(false);
                }
            }

            async Task PumpResponsesAsync(WebSocket webSocket, IDuplexStreamCall streamCall, byte[] tempBuffer, CancellationToken cancellationToken)
            {
                try
                {
                    await foreach (var payload in streamCall.ResponseReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await HttpDuplexProtocol.SendFrameAsync(webSocket, HttpDuplexProtocol.FrameType.ResponseData, payload, cancellationToken).ConfigureAwait(false);
                    }

                    var finalMeta = NormalizeResponseMeta(streamCall.ResponseMeta, transport);
                    var completePayload = HttpDuplexProtocol.SerializeResponseMeta(finalMeta);
                    await HttpDuplexProtocol.SendFrameAsync(webSocket, HttpDuplexProtocol.FrameType.ResponseComplete, completePayload, CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    var error = PolymerErrorAdapter.FromStatus(
                        PolymerStatusCode.Cancelled,
                        "The client cancelled the response stream.",
                        transport: transport);
                    await HttpDuplexProtocol.SendFrameAsync(webSocket, HttpDuplexProtocol.FrameType.ResponseError, HttpDuplexProtocol.CreateErrorPayload(error), CancellationToken.None).ConfigureAwait(false);
                    await streamCall.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var error = PolymerErrorAdapter.FromStatus(
                        PolymerStatusCode.Internal,
                        ex.Message ?? "An error occurred while writing the duplex response stream.",
                        transport: transport,
                        inner: Error.FromException(ex));
                    await HttpDuplexProtocol.SendFrameAsync(webSocket, HttpDuplexProtocol.FrameType.ResponseError, HttpDuplexProtocol.CreateErrorPayload(error), CancellationToken.None).ConfigureAwait(false);
                    await streamCall.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            CompleteRequest();
        }
    }
    private static ResponseMeta NormalizeResponseMeta(ResponseMeta? meta, string transport)
    {
        meta ??= new ResponseMeta();
        var headers = meta.Headers is { Count: > 0 } ? meta.Headers : null;
        return new ResponseMeta(
            encoding: meta.Encoding,
            transport: string.IsNullOrEmpty(meta.Transport) ? transport : meta.Transport,
            ttl: meta.Ttl,
            headers: headers);
    }

    private static string? ResolveRequestEncoding(IHeaderDictionary headers, string? contentType)
    {
        if (headers.TryGetValue(HttpTransportHeaders.Encoding, out var encodingValues) &&
            !StringValues.IsNullOrEmpty(encodingValues))
        {
            return encodingValues![0];
        }

        if (!string.IsNullOrEmpty(contentType))
        {
            return contentType;
        }

        return null;
    }

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

        return ProtobufEncoding.GetMediaType(encoding) ?? encoding;
    }

    private static RequestMeta BuildRequestMeta(
        string service,
        string procedure,
        string? encoding,
        IHeaderDictionary headers,
        string transport,
        CancellationToken cancellationToken)
    {
        TimeSpan? ttl = null;
        if (headers.TryGetValue(HttpTransportHeaders.TtlMs, out var ttlValues) &&
            long.TryParse(ttlValues![0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ttlMs))
        {
            ttl = TimeSpan.FromMilliseconds(ttlMs);
        }

        DateTimeOffset? deadline = null;
        if (headers.TryGetValue(HttpTransportHeaders.Deadline, out var deadlineValues) &&
            DateTimeOffset.TryParse(deadlineValues![0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDeadline))
        {
            deadline = parsedDeadline;
        }

        var metaHeaders = headers.Select(static header => new KeyValuePair<string, string>(header.Key, header.Value.ToString()));

        return new RequestMeta(
            service,
            procedure,
            caller: headers.TryGetValue(HttpTransportHeaders.Caller, out var callerValues) ? callerValues![0] : null,
            encoding: encoding,
            transport: transport,
            shardKey: headers.TryGetValue(HttpTransportHeaders.ShardKey, out var shardValues) ? shardValues![0] : null,
            routingKey: headers.TryGetValue(HttpTransportHeaders.RoutingKey, out var routingValues) ? routingValues![0] : null,
            routingDelegate: headers.TryGetValue(HttpTransportHeaders.RoutingDelegate, out var routingDelegateValues) ? routingDelegateValues![0] : null,
            timeToLive: ttl,
            deadline: deadline,
            headers: metaHeaders);
    }

    private static async Task WriteErrorAsync(
        HttpContext context,
        string message,
        PolymerStatusCode status,
        string transport,
        Error? error = null)
    {
        context.Response.StatusCode = HttpStatusMapper.ToStatusCode(status);
        context.Response.Headers[HttpTransportHeaders.Transport] = transport;
        context.Response.Headers[HttpTransportHeaders.Status] = status.ToString();
        context.Response.Headers[HttpTransportHeaders.ErrorMessage] = message;
        if (error is not null && !string.IsNullOrEmpty(error.Code))
        {
            context.Response.Headers[HttpTransportHeaders.ErrorCode] = error.Code;
        }

        context.Response.ContentType = MediaTypeNames.Application.Json;

        var payload = new
        {
            message,
            status = status.ToString(),
            code = error?.Code,
            metadata = error?.Metadata
        };

        await JsonSerializer.SerializeAsync(context.Response.Body, payload, cancellationToken: context.RequestAborted).ConfigureAwait(false);
    }

    private static ReadOnlyMemory<byte> EncodeSseFrame(ReadOnlyMemory<byte> payload, string? encoding)
    {
        if (!string.IsNullOrEmpty(encoding) && encoding.StartsWith("text", StringComparison.OrdinalIgnoreCase))
        {
            var text = Encoding.UTF8.GetString(payload.Span);
            var eventFrame = $"data: {text}\n\n";
            return Encoding.UTF8.GetBytes(eventFrame);
        }
        else
        {
            var base64 = Convert.ToBase64String(payload.Span);
            var eventFrame = $"data: {base64}\nencoding: base64\n\n";
            return Encoding.UTF8.GetBytes(eventFrame);
        }
    }

}
