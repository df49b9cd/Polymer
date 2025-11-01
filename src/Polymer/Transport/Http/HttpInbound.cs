using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Immutable;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Hugo;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Dispatcher;
using Polymer.Errors;

namespace Polymer.Transport.Http;

public sealed class HttpInbound : ILifecycle, IDispatcherAware
{
    private readonly string[] _urls;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly Action<WebApplication>? _configureApp;
    private WebApplication? _app;
    private Dispatcher.Dispatcher? _dispatcher;

    public HttpInbound(
        IEnumerable<string> urls,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null)
    {
        if (urls is null)
        {
            throw new ArgumentNullException(nameof(urls));
        }

        _urls = [.. urls];
        if (_urls.Length == 0)
        {
            throw new ArgumentException("At least one URL must be provided for the HTTP inbound.", nameof(urls));
        }

        _configureServices = configureServices;
        _configureApp = configureApp;
    }

    public IReadOnlyCollection<string> Urls =>
        _app?.Urls as IReadOnlyCollection<string> ?? Array.Empty<string>();

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

        _configureApp?.Invoke(app);

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

        await _app.StopAsync(cancellationToken).ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
    }

    private async Task HandleUnaryAsync(HttpContext context)
    {
        var dispatcher = _dispatcher!;
        var transport = "http";

        if (!context.Request.Headers.TryGetValue(HttpTransportHeaders.Procedure, out var procedureValues) ||
            StringValues.IsNullOrEmpty(procedureValues))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteErrorAsync(context, "rpc procedure header missing", PolymerStatusCode.InvalidArgument, transport).ConfigureAwait(false);
            return;
        }

        var procedure = procedureValues![0];

        string? encoding = null;
        if (context.Request.Headers.TryGetValue(HttpTransportHeaders.Encoding, out var encodingValues) &&
            !StringValues.IsNullOrEmpty(encodingValues))
        {
            encoding = encodingValues![0];
        }
        else if (!string.IsNullOrEmpty(context.Request.ContentType))
        {
            encoding = context.Request.ContentType;
        }

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
            foreach (var header in onewayResult.Value.Meta.Headers)
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
        context.Response.Headers[HttpTransportHeaders.Encoding] = response.Meta.Encoding ?? encoding ?? MediaTypeNames.Application.Octet;
        context.Response.Headers[HttpTransportHeaders.Transport] = transport;

        foreach (var header in response.Meta.Headers)
        {
            context.Response.Headers[header.Key] = header.Value;
        }

        if (!response.Body.IsEmpty)
        {
            await context.Response.BodyWriter.WriteAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private async Task HandleServerStreamAsync(HttpContext context)
    {
        var dispatcher = _dispatcher!;
        var transport = "http";

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
            encoding: context.Request.Headers.TryGetValue(HttpTransportHeaders.Encoding, out var encodingValues) && !StringValues.IsNullOrEmpty(encodingValues)
                ? encodingValues![0]
                : null,
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

        await using var call = streamResult.Value;

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers[HttpTransportHeaders.Transport] = transport;
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["Connection"] = "keep-alive";
        context.Response.Headers["Content-Type"] = "text/event-stream";

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
            var text = System.Text.Encoding.UTF8.GetString(payload.Span);
            var eventFrame = $"data: {text}\n\n";
            return System.Text.Encoding.UTF8.GetBytes(eventFrame);
        }
        else
        {
            var base64 = Convert.ToBase64String(payload.Span);
            var eventFrame = $"data: {base64}\nencoding: base64\n\n";
            return System.Text.Encoding.UTF8.GetBytes(eventFrame);
        }
    }
}
