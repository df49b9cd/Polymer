using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text.Json;
using Hugo;
using Microsoft.AspNetCore.Http;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Errors;
using Polymer.Transport.Http.Middleware;
using static Hugo.Go;

namespace Polymer.Transport.Http;

public sealed class HttpOutbound(HttpClient httpClient, Uri requestUri, bool disposeClient = false) : IUnaryOutbound, IOnewayOutbound, IOutboundDiagnostic, IHttpOutboundMiddlewareSink
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly Uri _requestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
    private readonly bool _disposeClient = disposeClient;
    private HttpOutboundMiddlewareRegistry? _middlewareRegistry;
    private string? _middlewareService;
    private int _middlewareConfigured;
    private ConcurrentDictionary<string, HttpClientMiddlewareDelegate>? _middlewarePipelines;

    public ValueTask StartAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallUnaryAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var httpRequest = BuildHttpRequest(request);
            using var response = await SendWithMiddlewareAsync(
                    httpRequest,
                    request.Meta,
                    HttpOutboundCallKind.Unary,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            var responseMeta = BuildResponseMeta(response);
            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return Ok(Response<ReadOnlyMemory<byte>>.Create(payload, responseMeta));
            }

            var error = await ReadErrorAsync(response, "http", cancellationToken).ConfigureAwait(false);
            return Err<Response<ReadOnlyMemory<byte>>>(error);
        }
        catch (Exception ex)
        {
            return PolymerErrors.ToResult<Response<ReadOnlyMemory<byte>>>(ex, transport: "http");
        }
    }

    private async ValueTask<Result<OnewayAck>> CallOnewayAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var httpRequest = BuildHttpRequest(request);
            using var response = await SendWithMiddlewareAsync(
                    httpRequest,
                    request.Meta,
                    HttpOutboundCallKind.Oneway,
                    HttpCompletionOption.ResponseContentRead,
                    cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Accepted ||
                response.StatusCode == (System.Net.HttpStatusCode)StatusCodes.Status202Accepted)
            {
                var meta = BuildResponseMeta(response);
                return Ok(OnewayAck.Ack(meta));
            }

            var error = await ReadErrorAsync(response, "http", cancellationToken).ConfigureAwait(false);
            return Err<OnewayAck>(error);
        }
        catch (Exception ex)
        {
            return PolymerErrors.ToResult<OnewayAck>(ex, transport: "http");
        }
    }

    private HttpRequestMessage BuildHttpRequest(IRequest<ReadOnlyMemory<byte>> request)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, _requestUri);
        var content = new ByteArrayContent(request.Body.ToArray());

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

        response.Headers.TryGetValues(HttpTransportHeaders.Encoding, out var encodingValues);
        var encoding = encodingValues?.FirstOrDefault() ?? response.Content.Headers.ContentType?.MediaType;
        encoding = ProtobufEncoding.Normalize(encoding);

        return new ResponseMeta(
            encoding: encoding,
            transport: "http",
            headers: headers);
    }

    private static async Task<Error> ReadErrorAsync(HttpResponseMessage response, string transport, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
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
                                 Enum.TryParse(statusProperty.GetString(), out PolymerStatusCode parsedStatus)
                        ? parsedStatus
                        : HttpStatusMapper.FromStatusCode((int)response.StatusCode);

                    var error = PolymerErrorAdapter.FromStatus(status, message, transport: transport);
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
        return PolymerErrorAdapter.FromStatus(fallbackStatus, fallbackMessage, transport: transport);
    }

    async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> IUnaryOutbound.CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken) =>
        await CallUnaryAsync(request, cancellationToken).ConfigureAwait(false);

    async ValueTask<Result<OnewayAck>> IOnewayOutbound.CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken) =>
        await CallOnewayAsync(request, cancellationToken).ConfigureAwait(false);

    public object? GetOutboundDiagnostics() =>
        new HttpOutboundSnapshot(_requestUri, _disposeClient);

    void IHttpOutboundMiddlewareSink.Attach(string service, HttpOutboundMiddlewareRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (Interlocked.Exchange(ref _middlewareConfigured, 1) == 1)
        {
            return;
        }

        _middlewareService = string.IsNullOrWhiteSpace(service) ? string.Empty : service;
        _middlewareRegistry = registry;
        _middlewarePipelines = new ConcurrentDictionary<string, HttpClientMiddlewareDelegate>(StringComparer.OrdinalIgnoreCase);
    }

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
        HttpClientMiddlewareDelegate pipeline;

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

        HttpClientMiddlewareDelegate ComposePipeline(IReadOnlyList<IHttpClientMiddleware> source)
        {
            return HttpClientMiddlewareComposer.Compose(source, Terminal);
        }

        ValueTask<HttpResponseMessage> Terminal(HttpClientMiddlewareContext ctx, CancellationToken token) =>
            new(_httpClient.SendAsync(ctx.Request, ctx.CompletionOption, token));
    }

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

public sealed record HttpOutboundSnapshot(Uri RequestUri, bool DisposesClient);
