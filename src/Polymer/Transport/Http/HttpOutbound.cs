using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Errors;
using Hugo;
using static Hugo.Go;

namespace Polymer.Transport.Http;

public sealed class HttpOutbound(HttpClient httpClient, Uri requestUri, bool disposeClient = false) : IUnaryOutbound, IOnewayOutbound, IOutboundDiagnostic
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly Uri _requestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
    private readonly bool _disposeClient = disposeClient;

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
            using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
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
            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

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

        if (!string.IsNullOrEmpty(request.Meta.Encoding))
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(request.Meta.Encoding);
        }

        httpRequest.Content = content;

        httpRequest.Headers.Add(HttpTransportHeaders.Transport, "http");
        httpRequest.Headers.Add(HttpTransportHeaders.Procedure, request.Meta.Procedure ?? string.Empty);

        if (!string.IsNullOrEmpty(request.Meta.Caller)) httpRequest.Headers.Add(HttpTransportHeaders.Caller, request.Meta.Caller);
        if (!string.IsNullOrEmpty(request.Meta.Encoding)) httpRequest.Headers.Add(HttpTransportHeaders.Encoding, request.Meta.Encoding);
        if (!string.IsNullOrEmpty(request.Meta.ShardKey)) httpRequest.Headers.Add(HttpTransportHeaders.ShardKey, request.Meta.ShardKey);
        if (!string.IsNullOrEmpty(request.Meta.RoutingKey)) httpRequest.Headers.Add(HttpTransportHeaders.RoutingKey, request.Meta.RoutingKey);
        if (!string.IsNullOrEmpty(request.Meta.RoutingDelegate)) httpRequest.Headers.Add(HttpTransportHeaders.RoutingDelegate, request.Meta.RoutingDelegate);

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
}

public sealed record HttpOutboundSnapshot(Uri RequestUri, bool DisposesClient);
