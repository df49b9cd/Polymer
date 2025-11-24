using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net.Mime;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Hugo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Quic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using OmniRelay.ControlPlane.Upgrade;
using OmniRelay.Core;
using OmniRelay.Core.Diagnostics;
using OmniRelay.Core.Extensions;
using OmniRelay.Core.Gossip;
using OmniRelay.Core.Transport;
using OmniRelay.Diagnostics;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Security.Authorization;
using OmniRelay.Transport.Security;
using static Hugo.Go;

namespace OmniRelay.Transport.Http;

/// <summary>
/// Hosts the OmniRelay HTTP inbound server exposing RPC endpoints, introspection, and health probes.
/// Supports HTTP/1.1 and HTTP/2, and can enable HTTP/3 (QUIC) when configured with TLS 1.3.
/// </summary>
public sealed partial class HttpInbound : ILifecycle, IDispatcherAware, INodeDrainParticipant
{
    private readonly Uri[] _urls;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly Action<WebApplication>? _configureApp;
    private readonly HttpServerTlsOptions? _serverTlsOptions;
    private readonly HttpServerRuntimeOptions? _serverRuntimeOptions;
    private readonly TransportSecurityPolicyEvaluator? _transportSecurity;
    private readonly MeshAuthorizationEvaluator? _authorization;
    private WebApplication? _app;
    private Dispatcher.Dispatcher? _dispatcher;
    private volatile bool _isDraining;
    private readonly WaitGroup _activeRequests = new();
    private int _activeRequestCount;
    private readonly object _contextLock = new();
    private readonly HashSet<HttpContext> _activeHttpContexts = [];
    private static readonly HttpInboundJsonContext JsonContext = HttpInboundJsonContext.Default;
    private static readonly PathString ControlPeersPath = new("/control/peers");
    private static readonly PathString ControlPeersAltPath = new("/omnirelay/control/peers");
    private static readonly PathString ControlExtensionsPath = new("/control/extensions");
    private static readonly PathString ControlExtensionsAltPath = new("/omnirelay/control/extensions");
    private const string RetryAfterHeaderValue = "1";
    private const int DefaultDuplexFrameBytes = 16 * 1024;
    private const string HttpTransportName = "http";
    private const string Http3ProtocolName = "http3";

    private static readonly Error UrlsRequiredError = Error.From("At least one URL must be provided for the HTTP inbound.", "http.inbound.urls_missing");

    public static Result<HttpInbound> TryCreate(
        IEnumerable<Uri> urls,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null,
        HttpServerRuntimeOptions? serverRuntimeOptions = null,
        HttpServerTlsOptions? serverTlsOptions = null,
        TransportSecurityPolicyEvaluator? transportSecurity = null,
        MeshAuthorizationEvaluator? authorizationEvaluator = null)
    {
        if (urls is null)
        {
            return Err<HttpInbound>(UrlsRequiredError);
        }

        return TryCreate(
            urls.Select(static uri => uri?.ToString() ?? string.Empty),
            configureServices,
            configureApp,
            serverRuntimeOptions,
            serverTlsOptions,
            transportSecurity,
            authorizationEvaluator);
    }

    public static Result<HttpInbound> TryCreate(
        IEnumerable<string> urls,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null,
        HttpServerRuntimeOptions? serverRuntimeOptions = null,
        HttpServerTlsOptions? serverTlsOptions = null,
        TransportSecurityPolicyEvaluator? transportSecurity = null,
        MeshAuthorizationEvaluator? authorizationEvaluator = null)
    {
        if (urls is null)
        {
            return Err<HttpInbound>(UrlsRequiredError);
        }

        var list = urls.ToArray();
        if (list.Length == 0)
        {
            return Err<HttpInbound>(UrlsRequiredError);
        }

        try
        {
            return Ok(new HttpInbound(
                list.Select(u => new Uri(u, UriKind.Absolute)),
                configureServices,
                configureApp,
                serverRuntimeOptions,
                serverTlsOptions,
                transportSecurity,
                authorizationEvaluator));
        }
        catch (Exception ex)
        {
            return Err<HttpInbound>(Error.FromException(ex));
        }
    }

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "http inbound: transport={Transport} protocol={Protocol} enabled on {EndpointCount} endpoint(s): {Endpoints}")]
    private static partial void LogHttp3Enabled(ILogger logger, string transport, string protocol, int endpointCount, string endpoints);

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "http inbound: transport={Transport} protocol={Protocol} was requested but no HTTPS endpoints were configured; falling back to HTTP/1.1 and HTTP/2.")]
    private static partial void LogHttp3Fallback(ILogger logger, string transport, string protocol);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Warning,
        Message = "http inbound: transport={Transport} protocol={Protocol} stream limit tuning is not supported by the current MsQuic transport; configured values will be ignored.")]
    private static partial void LogHttp3StreamLimitUnsupported(ILogger logger, string transport, string protocol);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Warning,
        Message = "http inbound: transport={Transport} protocol={Protocol} MsQuic tuning unsupported for {OptionList}; configured values will be ignored.")]
    private static partial void LogHttp3TuningUnsupported(ILogger logger, string transport, string protocol, string optionList);

    /// <summary>
    /// Creates a new HTTP inbound server with optional DI and app configuration hooks.
    /// </summary>
    /// <param name="urls">The URLs to bind (http/https).</param>
    /// <param name="configureServices">Optional service collection configuration.</param>
    /// <param name="configureApp">Optional application pipeline configuration.</param>
    /// <param name="serverRuntimeOptions">Kestrel and HTTP/3 runtime options.</param>
    /// <param name="serverTlsOptions">TLS options including certificate for HTTPS/HTTP/3.</param>
    /// <param name="transportSecurity"></param>
    /// <param name="authorizationEvaluator"></param>
    public HttpInbound(
        IEnumerable<string> urls,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null,
        HttpServerRuntimeOptions? serverRuntimeOptions = null,
        HttpServerTlsOptions? serverTlsOptions = null,
        TransportSecurityPolicyEvaluator? transportSecurity = null,
        MeshAuthorizationEvaluator? authorizationEvaluator = null)
        : this(urls.Select(u => new Uri(u, UriKind.Absolute)), configureServices, configureApp, serverRuntimeOptions, serverTlsOptions, transportSecurity, authorizationEvaluator)
    {
    }

    public HttpInbound(
        IEnumerable<Uri> urls,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null,
        HttpServerRuntimeOptions? serverRuntimeOptions = null,
        HttpServerTlsOptions? serverTlsOptions = null,
        TransportSecurityPolicyEvaluator? transportSecurity = null,
        MeshAuthorizationEvaluator? authorizationEvaluator = null)
    {
        ArgumentNullException.ThrowIfNull(urls);

        _urls = urls.Select(u => u ?? throw new ArgumentException("Inbound URL cannot be null.", nameof(urls))).ToArray();
        if (_urls.Length == 0)
        {
            throw new ArgumentException("At least one URL must be provided for the HTTP inbound.", nameof(urls));
        }

        _configureServices = configureServices;
        _configureApp = configureApp;
        _serverRuntimeOptions = serverRuntimeOptions;
        _serverTlsOptions = serverTlsOptions;
        _transportSecurity = transportSecurity;
        _authorization = authorizationEvaluator;
    }

    /// <summary>
    /// Gets the actual bound URLs after the server has started.
    /// </summary>
    public IReadOnlyCollection<string> Urls =>
        _app?.Urls as IReadOnlyCollection<string> ?? [];

    /// <summary>
    /// Binds the dispatcher used to route RPC procedures.
    /// </summary>
    /// <param name="dispatcher">The dispatcher instance.</param>
    public void Bind(Dispatcher.Dispatcher dispatcher) => _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    /// <summary>
    /// Starts the HTTP server and begins accepting requests.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
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

        var requiresHttps = _urls.Any(static uri =>
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

        var missingCertificate = _serverTlsOptions?.Certificate is null;

        if (requiresHttps && missingCertificate)
        {
            throw new InvalidOperationException("HTTPS binding requested but no HTTP server TLS certificate was configured.");
        }

        var builder = WebApplication.CreateSlimBuilder();
        var enableHttp3 = _serverRuntimeOptions?.EnableHttp3 == true;
        var http3Endpoints = enableHttp3 ? new List<string>() : null;
        var http3RuntimeOptions = _serverRuntimeOptions?.Http3;
        var streamLimitUnsupported = false;
        var idleTimeoutUnsupported = false;
        var keepAliveUnsupported = false;

        if (enableHttp3)
        {
            builder.WebHost.UseQuic(quicOptions =>
            {
                var idleTimeout = http3RuntimeOptions?.IdleTimeout ?? _serverRuntimeOptions?.KeepAliveTimeout;
                var idleTimeoutLabel = http3RuntimeOptions?.IdleTimeout is not null
                    ? "HTTP/3 IdleTimeout"
                    : _serverRuntimeOptions?.KeepAliveTimeout is not null
                        ? "HTTP keepAliveTimeout"
                        : "HTTP/3 IdleTimeout";

                if (idleTimeout is { } configuredIdleTimeout)
                {
                    if (configuredIdleTimeout <= TimeSpan.Zero)
                    {
                        throw new InvalidOperationException($"{idleTimeoutLabel} must be greater than zero.");
                    }

                    if (!TrySetQuicOption(quicOptions, "IdleTimeout", configuredIdleTimeout))
                    {
                        idleTimeoutUnsupported = true;
                    }
                }

                if (http3RuntimeOptions?.KeepAliveInterval is { } keepAliveInterval)
                {
                    if (keepAliveInterval <= TimeSpan.Zero)
                    {
                        throw new InvalidOperationException("HTTP/3 KeepAliveInterval must be greater than zero.");
                    }

                    if (!TrySetQuicOption(quicOptions, "KeepAliveInterval", keepAliveInterval))
                    {
                        keepAliveUnsupported = true;
                    }
                }

                if (http3RuntimeOptions?.MaxBidirectionalStreams is { } maxBidirectionalStreams)
                {
                    if (maxBidirectionalStreams <= 0)
                    {
                        throw new InvalidOperationException("HTTP/3 MaxBidirectionalStreams must be greater than zero.");
                    }

                    if (!TrySetQuicOption(quicOptions, "MaxBidirectionalStreamCount", maxBidirectionalStreams))
                    {
                        streamLimitUnsupported = true;
                    }
                }

                if (http3RuntimeOptions?.MaxUnidirectionalStreams is { } maxUnidirectionalStreams)
                {
                    if (maxUnidirectionalStreams <= 0)
                    {
                        throw new InvalidOperationException("HTTP/3 MaxUnidirectionalStreams must be greater than zero.");
                    }

                    if (!TrySetQuicOption(quicOptions, "MaxUnidirectionalStreamCount", maxUnidirectionalStreams))
                    {
                        streamLimitUnsupported = true;
                    }
                }
            });
        }

        builder.WebHost.UseKestrel(options =>
        {
            if (_serverRuntimeOptions?.MaxRequestBodySize is { } maxRequest)
            {
                options.Limits.MaxRequestBodySize = maxRequest;
            }
            if (_serverRuntimeOptions?.MaxRequestLineSize is { } maxRequestLine)
            {
                options.Limits.MaxRequestLineSize = maxRequestLine;
            }
            if (_serverRuntimeOptions?.MaxRequestHeadersTotalSize is { } maxRequestHeaders)
            {
                options.Limits.MaxRequestHeadersTotalSize = maxRequestHeaders;
                if (enableHttp3)
                {
                    try
                    {
                        options.Limits.Http3.MaxRequestHeaderFieldSize = maxRequestHeaders;
                    }
                    catch (ArgumentOutOfRangeException ex)
                    {
                        throw new InvalidOperationException("HTTP/3 MaxRequestHeaderFieldSize must be greater than zero.", ex);
                    }
                }
            }
            if (_serverRuntimeOptions?.KeepAliveTimeout is { } keepAliveTimeout)
            {
                options.Limits.KeepAliveTimeout = keepAliveTimeout;
            }
            if (_serverRuntimeOptions?.RequestHeadersTimeout is { } requestHeadersTimeout)
            {
                options.Limits.RequestHeadersTimeout = requestHeadersTimeout;
            }

            foreach (var uri in _urls)
            {
                var host = string.Equals(uri.Host, "*", StringComparison.Ordinal) ? System.Net.IPAddress.Any : System.Net.IPAddress.Parse(uri.Host);
                options.Listen(host, uri.Port, listenOptions =>
                {
                    if (enableHttp3)
                    {
                        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"HTTP/3 requires HTTPS. Update inbound URL '{uri}' to use https:// or disable HTTP/3 for this listener.");
                        }

                        Http3RuntimeGuards.EnsureServerSupport(uri.ToString(), _serverTlsOptions?.Certificate);

                        listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
                        var enableAltSvc = http3RuntimeOptions?.EnableAltSvc;
                        listenOptions.DisableAltSvcHeader = enableAltSvc switch
                        {
                            true => false,
                            false => true,
                            _ => false
                        };

                        http3Endpoints?.Add(uri.ToString());
                    }
                    else
                    {
                        listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                    }

                    if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    {
                        if (_serverTlsOptions?.Certificate is null)
                        {
                            throw new InvalidOperationException($"HTTPS binding requested for '{uri}' but no HTTP server TLS certificate was configured.");
                        }

                        var httpsOptions = new HttpsConnectionAdapterOptions
                        {
                            ServerCertificate = _serverTlsOptions.Certificate,
                            ClientCertificateMode = _serverTlsOptions.ClientCertificateMode
                        };

                        if (_serverTlsOptions.CheckCertificateRevocation is { } checkRevocation)
                        {
                            httpsOptions.CheckCertificateRevocation = checkRevocation;
                        }

                        if (enableHttp3)
                        {
                            httpsOptions.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                        }

                        listenOptions.UseHttps(httpsOptions);
                    }
                });
            }
        });

        builder.Services.AddRouting();
        builder.Services.TryAddSingleton<ExtensionRegistry>();
        builder.Services.TryAddSingleton<IExtensionDiagnosticsProvider>(sp => sp.GetRequiredService<ExtensionRegistry>());
        _configureServices?.Invoke(builder.Services);
        builder.Services.AddSingleton(_dispatcher);

        var app = builder.Build();

        app.MapGet(ControlExtensionsPath, HandleExtensionDiagnosticsAsync);
        app.MapGet(ControlExtensionsAltPath, HandleExtensionDiagnosticsAsync);

        if (enableHttp3)
        {
            if (http3Endpoints is { Count: > 0 })
            {
                LogHttp3Enabled(
                    app.Logger,
                    HttpTransportName,
                    Http3ProtocolName,
                    http3Endpoints.Count,
                    string.Join(", ", http3Endpoints));
            }
            else
            {
                LogHttp3Fallback(app.Logger, HttpTransportName, Http3ProtocolName);
            }

            if (streamLimitUnsupported)
            {
                LogHttp3StreamLimitUnsupported(app.Logger, HttpTransportName, Http3ProtocolName);
            }

            if (idleTimeoutUnsupported || keepAliveUnsupported)
            {
                var unsupportedOptions = new List<string>(capacity: 2);

                if (idleTimeoutUnsupported)
                {
                    unsupportedOptions.Add("idle timeout");
                }

                if (keepAliveUnsupported)
                {
                    unsupportedOptions.Add("keep-alive interval");
                }

                LogHttp3TuningUnsupported(
                    app.Logger,
                    HttpTransportName,
                    Http3ProtocolName,
                    string.Join(" and ", unsupportedOptions));
            }
        }

        app.UseWebSockets();

        if (_transportSecurity is not null)
        {
            app.Use(async (context, next) =>
            {
                var decisionResult = _transportSecurity.Evaluate(TransportSecurityContext.FromHttpContext(HttpTransportName, context));
                if (decisionResult.IsFailure || !decisionResult.Value.IsAllowed)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    var host = context.Request.Host.HasValue ? context.Request.Host.Value : context.Connection.RemoteIpAddress?.ToString() ?? "*";
                    var decision = decisionResult.IsFailure
                        ? new TransportSecurityDecision(false, decisionResult.Error?.Message)
                        : decisionResult.Value;

                    if (decisionResult.IsFailure && decisionResult.Error is not null)
                    {
                        context.Response.Headers.Append("omnirelay-error-code", decisionResult.Error.Code ?? string.Empty);
                        foreach (var (key, value) in decisionResult.Error.Metadata)
                        {
                            context.Response.Headers.Append($"omnirelay-error-{key}", value?.ToString());
                        }
                    }

                    await HttpJsonWriter.WriteAsync(
                        context.Response,
                        decision.ToPayload(HttpTransportName, host),
                        HttpJsonContext.Default.TransportSecurityDecisionPayload,
                        context.RequestAborted).ConfigureAwait(false);
                    return;
                }

                await next().ConfigureAwait(false);
            });
        }

        if (_authorization is not null)
        {
            app.Use(async (context, next) =>
            {
                var decision = _authorization.Evaluate(HttpTransportName, context.Request.Path, context);
                if (!decision.IsAllowed)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    var responsePayload = new TransportAuthorizationResponse(HttpTransportName, decision.Reason ?? "authorization failure");
                    await HttpJsonWriter.WriteAsync(
                        context.Response,
                        responsePayload,
                        HttpJsonContext.Default.TransportAuthorizationResponse,
                        context.RequestAborted).ConfigureAwait(false);
                    return;
                }

                await next().ConfigureAwait(false);
            });
        }

        _configureApp?.Invoke(app);

        app.MapGet("/omnirelay/introspect", HandleIntrospectAsync);
        app.MapGet("/healthz", HandleHealthzAsync);
        app.MapGet("/readyz", HandleReadyzAsync);
        app.MapMethods("/{**_}", [HttpMethods.Post], HandleUnaryAsync);
        app.MapMethods("/{**_}", [HttpMethods.Get], HandleServerStreamAsync);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
    }

    /// <summary>
    /// Initiates graceful drain and stops the HTTP server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null)
        {
            return;
        }

        await WaitForDrainCompletionAsync(swallowCancellation: true, cancellationToken).ConfigureAwait(false);

        var cancellationRequested = cancellationToken.IsCancellationRequested;
        var stopToken = cancellationRequested ? CancellationToken.None : cancellationToken;

        if (cancellationRequested)
        {
            var lifetime = _app.Services.GetService<IHostApplicationLifetime>();
            lifetime?.StopApplication();
            AbortActiveRequests();
        }

        try
        {
            await _app.StopAsync(stopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignore cancellation during shutdown to force stop
        }

        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
        _isDraining = false;
        Interlocked.Exchange(ref _activeRequestCount, 0);
    }

    ValueTask INodeDrainParticipant.DrainAsync(CancellationToken cancellationToken) =>
        WaitForDrainCompletionAsync(swallowCancellation: false, cancellationToken);

    ValueTask INodeDrainParticipant.ResumeAsync(CancellationToken cancellationToken)
    {
        _isDraining = false;
        return ValueTask.CompletedTask;
    }

    private static bool TrySetQuicOption(QuicTransportOptions options, string propertyName, object value)
    {
        var property = typeof(QuicTransportOptions).GetProperty(propertyName);
        if (property is null || !property.CanWrite)
        {
            return false;
        }

        try
        {
            property.SetValue(options, value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryBeginRequest(HttpContext context)
    {
        if (_isDraining)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers["Retry-After"] = RetryAfterHeaderValue;
            context.Response.Headers[HttpTransportHeaders.Protocol] = context.Request.Protocol;
            return false;
        }

        _activeRequests.Add(1);
        Interlocked.Increment(ref _activeRequestCount);
        lock (_contextLock)
        {
            _activeHttpContexts.Add(context);
        }
        return true;
    }

    private void CompleteRequest(HttpContext context)
    {
        _activeRequests.Done();
        Interlocked.Decrement(ref _activeRequestCount);
        lock (_contextLock)
        {
            _activeHttpContexts.Remove(context);
        }
    }

    private async ValueTask WaitForDrainCompletionAsync(bool swallowCancellation, CancellationToken cancellationToken)
    {
        if (_app is null)
        {
            return;
        }

        if (!_isDraining)
        {
            _isDraining = true;
        }

        try
        {
            await _activeRequests.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (swallowCancellation)
        {
            // Fast shutdown requested; ignore cancellation.
        }
    }

    private void AbortActiveRequests()
    {
        List<HttpContext> contexts;
        lock (_contextLock)
        {
            contexts = [.. _activeHttpContexts];
        }

        foreach (var httpContext in contexts)
        {
            try
            {
                httpContext.Abort();
            }
            catch
            {
                // ignore abort failures
            }
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
                JsonContext.DispatcherIntrospection,
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

        var payload = new HealthPayload(
            healthy ? "ok" : "unavailable",
            readiness ? "ready" : "live",
            issues.Count == 0 ? [] : [.. issues],
            Math.Max(Volatile.Read(ref _activeRequestCount), 0),
            _isDraining);

        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                payload,
                JsonContext.HealthPayload,
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    private async Task HandleUnaryAsync(HttpContext context)
    {
        var dispatcher = _dispatcher!;
        const string transport = "http";
        var startTimestamp = Stopwatch.GetTimestamp();

        if (!TryBeginRequest(context))
        {
            return;
        }

        try
        {
            var decodeResult = await DecodeUnaryRequestAsync(
                    context,
                    dispatcher.ServiceName,
                    transport,
                    _serverRuntimeOptions?.MaxInMemoryDecodeBytes,
                    context.RequestAborted)
                .ConfigureAwait(false);

            if (decodeResult.IsFailure)
            {
                var error = decodeResult.Error!;
                var status = OmniRelayErrorAdapter.ToStatus(error);
                await WriteErrorAsync(context, error.Message ?? "invalid unary request", status, transport, error).ConfigureAwait(false);
                return;
            }

            var requestContext = decodeResult.Value;
            var procedure = requestContext.Procedure;
            var encoding = requestContext.Encoding;

            context.Response.Headers[HttpTransportHeaders.Transport] = transport;
            context.Response.Headers[HttpTransportHeaders.Protocol] = context.Request.Protocol;

            var activity = Activity.Current;
            if (activity is not null)
            {
                activity.SetTag("rpc.system", "http");
                activity.SetTag("rpc.service", dispatcher.ServiceName);
                activity.SetTag("rpc.method", procedure);
                activity.SetTag("rpc.protocol", context.Request.Protocol);
                if (context.Request.Protocol.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                {
                    activity.SetTag("network.protocol.name", "http");
                    var version = context.Request.Protocol.Length > 5 ? context.Request.Protocol[5..] : string.Empty;
                    if (!string.IsNullOrEmpty(version))
                    {
                        activity.SetTag("network.protocol.version", version);
                    }
                    activity.SetTag("network.transport", version.StartsWith('3') ? "quic" : "tcp");
                }
            }

            var baseTags = HttpTransportMetrics.CreateBaseTags(dispatcher.ServiceName, procedure, context.Request.Method, context.Request.Protocol);
            HttpTransportMetrics.RequestsStarted.Add(1, baseTags);

            void RecordMetrics(string outcome)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                var tags = HttpTransportMetrics.AppendOutcome(baseTags, context.Response.StatusCode, outcome);
                HttpTransportMetrics.RequestDuration.Record(elapsedMs, tags);
                HttpTransportMetrics.RequestsCompleted.Add(1, tags);
            }

            if (dispatcher.TryGetProcedure(procedure, ProcedureKind.Oneway, out _))
            {
                var onewayResult = await dispatcher.InvokeOnewayAsync(procedure, requestContext.Request, context.RequestAborted).ConfigureAwait(false);
                if (onewayResult.IsFailure)
                {
                    var error = onewayResult.Error!;
                    var exception = OmniRelayErrors.FromError(error, transport);
                    await WriteErrorAsync(context, exception.Message, exception.StatusCode, transport, error).ConfigureAwait(false);
                    RecordMetrics("error");
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status202Accepted;

                var ackMeta = onewayResult.Value.Meta;
                var ackEncoding = ackMeta.Encoding ?? encoding;
                context.Response.Headers[HttpTransportHeaders.Encoding] = ackEncoding ?? MediaTypeNames.Application.Octet;
                context.Response.ContentType = ResolveContentType(ackEncoding) ?? MediaTypeNames.Application.Octet;

                foreach (var header in ackMeta.Headers)
                {
                    if (string.Equals(header.Key, HttpTransportHeaders.Protocol, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    context.Response.Headers[header.Key] = header.Value;
                }

                RecordMetrics("success");
                return;
            }

            var result = await dispatcher.InvokeUnaryAsync(procedure, requestContext.Request, context.RequestAborted).ConfigureAwait(false);

            if (result.IsFailure)
            {
                var error = result.Error!;
                var exception = OmniRelayErrors.FromError(error, transport);
                await WriteErrorAsync(context, exception.Message, exception.StatusCode, transport, error).ConfigureAwait(false);
                RecordMetrics("error");
                return;
            }

            var response = result.Value;
            context.Response.StatusCode = StatusCodes.Status200OK;
            var responseEncoding = response.Meta.Encoding ?? encoding;
            context.Response.Headers[HttpTransportHeaders.Encoding] = responseEncoding ?? MediaTypeNames.Application.Octet;
            context.Response.ContentType = ResolveContentType(responseEncoding) ?? MediaTypeNames.Application.Octet;

            foreach (var header in response.Meta.Headers)
            {
                if (string.Equals(header.Key, HttpTransportHeaders.Protocol, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                context.Response.Headers[header.Key] = header.Value;
            }

            if (!response.Body.IsEmpty)
            {
                await context.Response.BodyWriter.WriteAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
            }

            RecordMetrics("success");
        }
        finally
        {
            CompleteRequest(context);
        }
    }

    private async Task HandleServerStreamAsync(HttpContext context)
    {
        var dispatcher = _dispatcher!;
        const string transport = "http";
        var startTimestamp = Stopwatch.GetTimestamp();

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
            if (IsPeerDiagnosticsRequest(context.Request.Path))
            {
                await HandlePeerDiagnosticsAsync(context).ConfigureAwait(false);
                return;
            }

            if (!AcceptsServerSentEvents(context.Request.Headers))
            {
                context.Response.StatusCode = StatusCodes.Status406NotAcceptable;
                await context.Response.WriteAsync("text/event-stream Accept header required for streaming", context.RequestAborted).ConfigureAwait(false);
                return;
            }

            var decodeResult = DecodeServerStreamRequest(context, dispatcher.ServiceName, transport);
            if (decodeResult.IsFailure)
            {
                var error = decodeResult.Error!;
                var status = OmniRelayErrorAdapter.ToStatus(error);
                await WriteErrorAsync(context, error.Message ?? "invalid stream request", status, transport, error).ConfigureAwait(false);
                return;
            }

            var requestContext = decodeResult.Value;
            context.Response.Headers[HttpTransportHeaders.Transport] = transport;
            context.Response.Headers[HttpTransportHeaders.Protocol] = context.Request.Protocol;

            var activity = Activity.Current;
            if (activity is not null)
            {
                activity.SetTag("rpc.system", "http");
                activity.SetTag("rpc.service", dispatcher.ServiceName);
                activity.SetTag("rpc.method", requestContext.Procedure);
                activity.SetTag("rpc.protocol", context.Request.Protocol);
                if (context.Request.Protocol.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                {
                    activity.SetTag("network.protocol.name", "http");
                    var version = context.Request.Protocol.Length > 5 ? context.Request.Protocol[5..] : string.Empty;
                    if (!string.IsNullOrEmpty(version))
                    {
                        activity.SetTag("network.protocol.version", version);
                    }
                    activity.SetTag("network.transport", version.StartsWith('3') ? "quic" : "tcp");
                }
            }

            var baseTags = HttpTransportMetrics.CreateBaseTags(dispatcher.ServiceName, requestContext.Procedure, context.Request.Method, context.Request.Protocol);
            HttpTransportMetrics.RequestsStarted.Add(1, baseTags);

            var streamResult = await dispatcher.InvokeStreamAsync(
                requestContext.Procedure,
                new Request<ReadOnlyMemory<byte>>(requestContext.Meta, ReadOnlyMemory<byte>.Empty),
                new StreamCallOptions(StreamDirection.Server),
                context.RequestAborted).ConfigureAwait(false);

            if (streamResult.IsFailure)
            {
                var error = streamResult.Error!;
                var exception = OmniRelayErrors.FromError(error, transport);
                context.Response.StatusCode = HttpStatusMapper.ToStatusCode(exception.StatusCode);
                await WriteErrorAsync(context, exception.Message, exception.StatusCode, transport, error).ConfigureAwait(false);
                var elapsedErr = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                var tagsErr = HttpTransportMetrics.AppendOutcome(baseTags, context.Response.StatusCode, "error");
                HttpTransportMetrics.RequestDuration.Record(elapsedErr, tagsErr);
                HttpTransportMetrics.RequestsCompleted.Add(1, tagsErr);
                return;
            }

            await using (streamResult.Value.AsAsyncDisposable(out var call))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers.Connection = "keep-alive";
                context.Response.Headers.ContentType = "text/event-stream";
                context.Response.Headers["X-Accel-Buffering"] = "no";

                var responseMeta = call.ResponseMeta ?? new ResponseMeta();
                var responseHeaders = responseMeta.Headers ?? [];
                foreach (var header in responseHeaders)
                {
                    if (string.Equals(header.Key, HttpTransportHeaders.Protocol, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    context.Response.Headers[header.Key] = header.Value;
                }

                var pumpResult = await PumpServerStreamAsync(
                        call,
                        context.Response.BodyWriter,
                        responseMeta,
                        _serverRuntimeOptions?.ServerStreamWriteTimeout,
                        _serverRuntimeOptions?.ServerStreamMaxMessageBytes,
                        transport,
                        baseTags,
                        context.RequestAborted)
                    .ConfigureAwait(false);

                var pumpError = pumpResult.Error;
                if (pumpError is not null)
                {
                    var pumpStatus = OmniRelayErrorAdapter.ToStatus(pumpError);
                    context.Response.StatusCode = HttpStatusMapper.ToStatusCode(pumpStatus);
                }

                var outcome = pumpResult.IsSuccess
                    ? "success"
                    : pumpError is { Code: var code } && string.Equals(code, ErrorCodes.Canceled, StringComparison.OrdinalIgnoreCase)
                        ? "cancelled"
                        : "error";

                var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                var tags = HttpTransportMetrics.AppendOutcome(baseTags, context.Response.StatusCode, outcome);
                HttpTransportMetrics.RequestDuration.Record(elapsed, tags);
                HttpTransportMetrics.RequestsCompleted.Add(1, tags);

                if (pumpError is not null)
                {
                    await call.CompleteAsync(pumpError, CancellationToken.None).ConfigureAwait(false);
                    context.Abort();
                }
            }
        }
        finally
        {
            CompleteRequest(context);
        }
    }

    private static ValueTask<Result<HttpUnaryRequestContext>> DecodeUnaryRequestAsync(
        HttpContext context,
        string serviceName,
        string transport,
        long? maxInMemory,
        CancellationToken cancellationToken)
    {
        return Go.Ok(context)
            .Ensure(ctx => HttpMethods.IsPost(ctx.Request.Method), _ => OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.InvalidArgument,
                "HTTP POST required for unary RPC.",
                transport: transport))
            .Then(ctx => ResolveProcedure(ctx, transport))
            .Map(procedure =>
            {
                var encoding = ResolveRequestEncoding(context.Request.Headers, context.Request.ContentType);
                var meta = BuildRequestMeta(serviceName, procedure, encoding, context.Request.Headers, transport, context.Request.Protocol);
                return new HttpUnaryDecodeState(procedure, encoding, meta);
            })
            .ThenValueTaskAsync<HttpUnaryDecodeState, HttpUnaryRequestContext>(async (state, token) =>
            {
                var bodyResult = await ReadRequestBodyAsync(context, transport, maxInMemory, token).ConfigureAwait(false);
                return bodyResult.Map(payload =>
                    new HttpUnaryRequestContext(state.Procedure, state.Meta, new Request<ReadOnlyMemory<byte>>(state.Meta, payload), state.Encoding));
            }, cancellationToken);
    }

    private static Result<HttpServerStreamRequestContext> DecodeServerStreamRequest(
        HttpContext context,
        string serviceName,
        string transport)
    {
        return Go.Ok(context)
            .Ensure(ctx => HttpMethods.IsGet(ctx.Request.Method), _ => OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.InvalidArgument,
                "HTTP GET required for streaming requests.",
                transport: transport))
            .Then(ctx => ResolveProcedure(ctx, transport))
            .Ensure(_ => AcceptsServerSentEvents(context.Request.Headers), _ => OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.InvalidArgument,
                "text/event-stream Accept header required for streaming",
                transport: transport))
            .Map(procedure =>
            {
                var encoding = ResolveRequestEncoding(context.Request.Headers, context.Request.ContentType);
                var meta = BuildRequestMeta(serviceName, procedure, encoding, context.Request.Headers, transport, context.Request.Protocol);
                return new HttpServerStreamRequestContext(procedure, meta);
            });
    }

    private static Result<string> ResolveProcedure(HttpContext context, string transport)
    {
        if (!context.Request.Headers.TryGetValue(HttpTransportHeaders.Procedure, out var procedureValues) ||
            StringValues.IsNullOrEmpty(procedureValues))
        {
            return Err<string>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.InvalidArgument,
                "rpc procedure header missing",
                transport: transport));
        }

        var procedure = procedureValues![0];
        return Ok(procedure ?? string.Empty);
    }

    private static bool AcceptsServerSentEvents(IHeaderDictionary headers)
    {
        var acceptValues = headers.TryGetValue("Accept", out var acceptRaw)
            ? acceptRaw
            : StringValues.Empty;

        if (acceptValues.Count == 0)
        {
            return false;
        }

        foreach (var value in acceptValues)
        {
            if (!string.IsNullOrEmpty(value) && value.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async ValueTask<Result<ReadOnlyMemory<byte>>> ReadRequestBodyAsync(
        HttpContext context,
        string transport,
        long? maxInMemory,
        CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.ContentLength is { } contentLength)
            {
                if (maxInMemory is { } max && contentLength > max)
                {
                    return Err<ReadOnlyMemory<byte>>(OmniRelayErrorAdapter.FromStatus(
                        OmniRelayStatusCode.ResourceExhausted,
                        "request body exceeds in-memory decode limit",
                        transport: transport));
                }

                if (contentLength == 0)
                {
                    return Ok(ReadOnlyMemory<byte>.Empty);
                }

                if (contentLength > int.MaxValue)
                {
                    return Err<ReadOnlyMemory<byte>>(OmniRelayErrorAdapter.FromStatus(
                        OmniRelayStatusCode.ResourceExhausted,
                        "request body too large",
                        transport: transport));
                }

                var buffer = new byte[(int)contentLength];
                await context.Request.Body.ReadExactlyAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                return Ok((ReadOnlyMemory<byte>)buffer);
            }

            const int chunkSize = 81920;
            long total = 0;
            var writer = new ArrayBufferWriter<byte>(chunkSize);
            var rented = ArrayPool<byte>.Shared.Rent(chunkSize);
            try
            {
                while (true)
                {
                    var read = await context.Request.Body.ReadAsync(rented.AsMemory(0, chunkSize), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;

                    if (maxInMemory is { } maxBytes && total > maxBytes)
                    {
                        return Err<ReadOnlyMemory<byte>>(OmniRelayErrorAdapter.FromStatus(
                            OmniRelayStatusCode.ResourceExhausted,
                            "request body exceeds in-memory decode limit",
                            transport: transport));
                    }

                    if (total > int.MaxValue)
                    {
                        return Err<ReadOnlyMemory<byte>>(OmniRelayErrorAdapter.FromStatus(
                            OmniRelayStatusCode.ResourceExhausted,
                            "request body too large",
                            transport: transport));
                    }

                    writer.Write(rented.AsSpan(0, read));
                }

                return Ok(writer.WrittenMemory);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
        catch (OperationCanceledException)
        {
            return Err<ReadOnlyMemory<byte>>(Error.Canceled());
        }
        catch (Exception ex)
        {
            return Err<ReadOnlyMemory<byte>>(OmniRelayErrors.FromException(ex, transport).Error ?? Error.FromException(ex));
        }
    }

    private static async ValueTask<Result<Unit>> PumpServerStreamAsync(
        IStreamCall call,
        PipeWriter writer,
        ResponseMeta responseMeta,
        TimeSpan? writeTimeout,
        int? maxMessageBytes,
        string transport,
        KeyValuePair<string, object?>[] metricTags,
        CancellationToken cancellationToken)
    {
        var stream = Result.MapStreamAsync(
            call.Responses.ReadAllAsync(cancellationToken),
            (payload, token) =>
            {
                if (maxMessageBytes is { } limit && payload.Length > limit)
                {
                    var error = OmniRelayErrorAdapter.FromStatus(
                        OmniRelayStatusCode.ResourceExhausted,
                        "The server stream payload exceeds the configured limit.",
                        transport: transport);
                    return new ValueTask<Result<ReadOnlyMemory<byte>>>(Err<ReadOnlyMemory<byte>>(error));
                }

                return new ValueTask<Result<ReadOnlyMemory<byte>>>(Ok(payload));
            },
            cancellationToken);

        var pumpResult = await stream
            .TapSuccessEachAsync(async (payload, token) =>
            {
                var frame = EncodeSseFrame(payload, responseMeta.Encoding);
                await WritePipeAsync(writer, frame, writeTimeout, token).ConfigureAwait(false);
                await FlushPipeAsync(writer, writeTimeout, token).ConfigureAwait(false);
                HttpTransportMetrics.ServerStreamResponseMessages.Add(1, metricTags);
            }, cancellationToken)
            .ConfigureAwait(false);

        if (pumpResult.IsFailure && pumpResult.Error is { } error)
        {
            await call.CompleteAsync(error, CancellationToken.None).ConfigureAwait(false);
        }

        return pumpResult.IsFailure && pumpResult.Error is null
            ? Err<Unit>(Error.Canceled())
            : pumpResult;
    }

    private readonly record struct HttpUnaryDecodeState(string Procedure, string? Encoding, RequestMeta Meta);

    private readonly record struct HttpUnaryRequestContext(
        string Procedure,
        RequestMeta Meta,
        Request<ReadOnlyMemory<byte>> Request,
        string? Encoding);

    private readonly record struct HttpServerStreamRequestContext(string Procedure, RequestMeta Meta);

    private static bool IsPeerDiagnosticsRequest(PathString path) =>
        path.HasValue &&
        (path.Equals(ControlPeersPath, StringComparison.OrdinalIgnoreCase) ||
         path.Equals(ControlPeersAltPath, StringComparison.OrdinalIgnoreCase));

    private static async Task HandlePeerDiagnosticsAsync(HttpContext context)
    {
        var provider = context.RequestServices.GetService<IPeerDiagnosticsProvider>();
        if (provider is null)
        {
            var membership = context.RequestServices.GetService<IMeshMembershipSnapshotProvider>();
            provider = membership is null
                ? NullPeerDiagnosticsProvider.Instance
                : new MeshPeerDiagnosticsProvider(membership);
        }

        var snapshot = provider.CreateSnapshot();
        var result = TypedResults.Json(snapshot, Diagnostics.DiagnosticsJsonContext.Default.PeerDiagnosticsResponse);
        await result.ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task HandleExtensionDiagnosticsAsync(HttpContext context)
    {
        var provider = context.RequestServices.GetService<IExtensionDiagnosticsProvider>();
        if (provider is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var snapshot = provider.CreateSnapshot();
        var result = TypedResults.Json(snapshot, ExtensionsJsonContext.Default.ExtensionDiagnosticsResponse);
        await result.ExecuteAsync(context).ConfigureAwait(false);
    }

    private async Task HandleDuplexAsync(HttpContext context)
    {
        var dispatcher = _dispatcher!;
        const string transport = "http";
        var startTimestamp = Stopwatch.GetTimestamp();

        if (!TryBeginRequest(context))
        {
            return;
        }

        try
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status406NotAcceptable;
                await context.Response.WriteAsync("WebSocket upgrade required for duplex streaming.", context.RequestAborted).ConfigureAwait(false);
                return;
            }

            if (!context.Request.Headers.TryGetValue(HttpTransportHeaders.Procedure, out var procedureValues) ||
                StringValues.IsNullOrEmpty(procedureValues))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteErrorAsync(context, "rpc procedure header missing", OmniRelayStatusCode.InvalidArgument, transport).ConfigureAwait(false);
                return;
            }

            var procedure = procedureValues![0];

            var meta = BuildRequestMeta(
                dispatcher.ServiceName,
                procedure!,
                encoding: ResolveRequestEncoding(context.Request.Headers, context.Request.ContentType),
                headers: context.Request.Headers,
                transport: transport,
                protocol: context.Request.Protocol);

            context.Response.Headers[HttpTransportHeaders.Transport] = transport;
            context.Response.Headers[HttpTransportHeaders.Protocol] = context.Request.Protocol;

            var dispatcherRequest = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

            // Tracing: enrich current Activity
            var activity = Activity.Current;
            if (activity is not null)
            {
                activity.SetTag("rpc.system", "http");
                activity.SetTag("rpc.service", dispatcher.ServiceName);
                activity.SetTag("rpc.method", procedure!);
                activity.SetTag("rpc.protocol", context.Request.Protocol);
                if (context.Request.Protocol.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                {
                    activity.SetTag("network.protocol.name", "http");
                    var version = context.Request.Protocol.Length > 5 ? context.Request.Protocol[5..] : string.Empty;
                    if (!string.IsNullOrEmpty(version))
                    {
                        activity.SetTag("network.protocol.version", version);
                    }
                    activity.SetTag("network.transport", version.StartsWith('3') ? "quic" : "tcp");
                }
            }

            // Metrics: request started (duplex)
            var baseTags = HttpTransportMetrics.CreateBaseTags(dispatcher.ServiceName, procedure!, context.Request.Method, context.Request.Protocol);
            HttpTransportMetrics.RequestsStarted.Add(1, baseTags);

            var callResult = await dispatcher.InvokeDuplexAsync(procedure!, dispatcherRequest, context.RequestAborted).ConfigureAwait(false);

            if (callResult.IsFailure)
            {
                var error = callResult.Error!;
                var exception = OmniRelayErrors.FromError(error, transport);
                context.Response.StatusCode = HttpStatusMapper.ToStatusCode(exception.StatusCode);
                await WriteErrorAsync(context, exception.Message, exception.StatusCode, transport, error).ConfigureAwait(false);
                var elapsedErr = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                var tagsErr = HttpTransportMetrics.AppendOutcome(baseTags, context.Response.StatusCode, "error");
                HttpTransportMetrics.RequestDuration.Record(elapsedErr, tagsErr);
                HttpTransportMetrics.RequestsCompleted.Add(1, tagsErr);
                return;
            }

            var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

            var call = callResult.Value;
            var configuredFrameLimit = _serverRuntimeOptions?.DuplexMaxFrameBytes;
            var duplexFrameLimit = configuredFrameLimit.HasValue && configuredFrameLimit.Value > 0
                ? Math.Min(configuredFrameLimit.Value, int.MaxValue - 1)
                : DefaultDuplexFrameBytes;
            var requestBuffer = new byte[duplexFrameLimit + 1];
            using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var duplexWriteTimeout = _serverRuntimeOptions?.DuplexWriteTimeout;
            Error? pumpError = null;

            try
            {
                using var pumpGroup = new ErrGroup(pumpCts.Token);

                pumpGroup.Go(async token =>
                {
                    await PumpRequestsAsync(socket, call, requestBuffer, duplexFrameLimit, baseTags, token).ConfigureAwait(false);
                    return Ok(Unit.Value);
                });

                pumpGroup.Go(async token =>
                {
                    await PumpResponsesAsync(socket, call, duplexFrameLimit, duplexWriteTimeout, baseTags, token).ConfigureAwait(false);
                    return Ok(Unit.Value);
                });

                var pumpResult = await pumpGroup.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                pumpError = pumpResult.Error;
            }
            finally
            {
                await call.DisposeAsync().ConfigureAwait(false);

                if (socket.State == WebSocketState.Open)
                {
                    try
                    {
                        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "completed", closeCts.Token).ConfigureAwait(false);
                    }
                    catch (WebSocketException)
                    {
                        // ignore handshake failures during shutdown
                    }
                    catch (OperationCanceledException)
                    {
                        socket.Abort();
                    }
                }

                socket.Dispose();
            }

            // Metrics: success completion (duplex)
            {
                var outcome = pumpError is null
                    ? "success"
                    : string.Equals(pumpError.Code, ErrorCodes.Canceled, StringComparison.OrdinalIgnoreCase)
                        ? "cancelled"
                        : "error";
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                var tags = HttpTransportMetrics.AppendOutcome(baseTags, context.Response.StatusCode, outcome);
                HttpTransportMetrics.RequestDuration.Record(elapsed, tags);
                HttpTransportMetrics.RequestsCompleted.Add(1, tags);
            }

            async ValueTask PumpRequestsAsync(WebSocket webSocket, IDuplexStreamCall streamCall, byte[] tempBuffer, int frameLimit, KeyValuePair<string, object?>[] metricTags, CancellationToken cancellationToken)
            {
                var frameChannel = Go.MakeChannel<Result<HttpDuplexProtocol.Frame>>(new BoundedChannelOptions(8)
                {
                    AllowSynchronousContinuations = false,
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait
                });

                var receivePump = new WaitGroup();
                receivePump.Go(async token =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested)
                        {
                            var frameResult = await HttpDuplexProtocol.ReceiveFrameAsync(
                                    webSocket,
                                    tempBuffer,
                                    frameLimit,
                                    transport,
                                    token)
                                .ConfigureAwait(false);

                            if (frameResult.IsSuccess && !frameResult.Value.Payload.IsEmpty)
                            {
                                var frame = frameResult.Value;
                                frameResult = Ok(new HttpDuplexProtocol.Frame(frame.MessageType, frame.Type, CopyFramePayload(frame.Payload)));
                            }

                            await frameChannel.Writer.WriteAsync(frameResult, token).ConfigureAwait(false);

                            if (frameResult.IsFailure || (frameResult.IsSuccess && frameResult.Value.MessageType == WebSocketMessageType.Close))
                            {
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        var canceled = OmniRelayErrorAdapter.FromStatus(
                            OmniRelayStatusCode.Cancelled,
                            "The client cancelled the request.",
                            transport: transport);
                        frameChannel.Writer.TryWrite(Err<HttpDuplexProtocol.Frame>(canceled));
                    }
                    catch (Exception ex)
                    {
                        var normalized = OmniRelayErrors.FromException(ex, transport).Error;
                        frameChannel.Writer.TryWrite(Err<HttpDuplexProtocol.Frame>(normalized));
                    }
                    finally
                    {
                        frameChannel.Writer.TryComplete();
                    }
                }, cancellationToken: cancellationToken);

                try
                {
                    await foreach (var frameResult in frameChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (frameResult.IsFailure)
                        {
                            var error = NormalizeDuplexError(frameResult.Error, transport);
                            await HttpDuplexProtocol.SendFrameResultAsync(
                                    webSocket,
                                    HttpDuplexProtocol.FrameType.RequestError,
                                    HttpDuplexProtocol.CreateErrorPayload(error),
                                    transport,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                            await streamCall.CompleteRequestsAsync(error, CancellationToken.None).ConfigureAwait(false);
                            await streamCall.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
                            pumpCts.Cancel();
                            return;
                        }

                        var frame = frameResult.Value;
                        if (frame.MessageType == WebSocketMessageType.Close)
                        {
                            await streamCall.CompleteRequestsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                            return;
                        }

                        switch (frame.Type)
                        {
                            case HttpDuplexProtocol.FrameType.RequestData:
                                await streamCall.RequestWriter.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
                                HttpTransportMetrics.DuplexRequestMessages.Add(1, metricTags);
                                break;

                            case HttpDuplexProtocol.FrameType.RequestComplete:
                                await streamCall.CompleteRequestsAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                                return;

                            case HttpDuplexProtocol.FrameType.RequestError:
                                {
                                    var error = HttpDuplexProtocol.ParseError(frame.Payload.Span, transport);
                                    await streamCall.CompleteRequestsAsync(error, CancellationToken.None).ConfigureAwait(false);
                                    await streamCall.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
                                    pumpCts.Cancel();
                                    return;
                                }

                            case HttpDuplexProtocol.FrameType.ResponseError:
                                {
                                    var error = HttpDuplexProtocol.ParseError(frame.Payload.Span, transport);
                                    await streamCall.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
                                    pumpCts.Cancel();
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
                    var canceled = OmniRelayErrorAdapter.FromStatus(
                        OmniRelayStatusCode.Cancelled,
                        "The client cancelled the request.",
                        transport: transport);
                    await streamCall.CompleteRequestsAsync(canceled, CancellationToken.None).ConfigureAwait(false);
                    pumpCts.Cancel();
                }
                catch (Exception ex)
                {
                    var error = NormalizeDuplexError(OmniRelayErrors.FromException(ex, transport).Error, transport);
                    await HttpDuplexProtocol.SendFrameResultAsync(
                            webSocket,
                            HttpDuplexProtocol.FrameType.RequestError,
                            HttpDuplexProtocol.CreateErrorPayload(error),
                            transport,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await streamCall.CompleteRequestsAsync(error, CancellationToken.None).ConfigureAwait(false);
                    await streamCall.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
                    pumpCts.Cancel();
                }
                finally
                {
                    await receivePump.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            async ValueTask PumpResponsesAsync(WebSocket webSocket, IDuplexStreamCall streamCall, int frameLimit, TimeSpan? writeTimeout, KeyValuePair<string, object?>[] metricTags, CancellationToken cancellationToken)
            {
                var responseStream = Result.MapStreamAsync(
                    streamCall.ResponseReader.ReadAllAsync(cancellationToken),
                    (payload, token) =>
                    {
                        if (frameLimit > 0 && payload.Length > frameLimit)
                        {
                            var error = OmniRelayErrorAdapter.FromStatus(
                                OmniRelayStatusCode.ResourceExhausted,
                                "The duplex response payload exceeds the configured limit.",
                                transport: transport);
                            return new ValueTask<Result<ReadOnlyMemory<byte>>>(Err<ReadOnlyMemory<byte>>(error));
                        }

                        return new ValueTask<Result<ReadOnlyMemory<byte>>>(Ok(payload));
                    },
                    cancellationToken);

                try
                {
                    await foreach (var payloadResult in responseStream.ConfigureAwait(false))
                    {
                        if (payloadResult.IsFailure)
                        {
                            var responseError = NormalizeDuplexError(payloadResult.Error, transport);
                            await HttpDuplexProtocol.SendFrameResultAsync(
                                    webSocket,
                                    HttpDuplexProtocol.FrameType.ResponseError,
                                    HttpDuplexProtocol.CreateErrorPayload(responseError),
                                    transport,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                            await streamCall.CompleteResponsesAsync(responseError, CancellationToken.None).ConfigureAwait(false);
                            await streamCall.CompleteRequestsAsync(responseError, CancellationToken.None).ConfigureAwait(false);
                            pumpCts.Cancel();
                            return;
                        }

                        var sendResult = await SendWebSocketFrameAsync(
                                webSocket,
                                HttpDuplexProtocol.FrameType.ResponseData,
                                payloadResult.Value,
                                writeTimeout,
                                transport,
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (sendResult.IsFailure)
                        {
                            var sendError = NormalizeDuplexError(sendResult.Error, transport);
                            await HttpDuplexProtocol.SendFrameResultAsync(
                                    webSocket,
                                    HttpDuplexProtocol.FrameType.ResponseError,
                                    HttpDuplexProtocol.CreateErrorPayload(sendError),
                                    transport,
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                            await streamCall.CompleteResponsesAsync(sendError, CancellationToken.None).ConfigureAwait(false);
                            await streamCall.CompleteRequestsAsync(sendError, CancellationToken.None).ConfigureAwait(false);
                            pumpCts.Cancel();
                            return;
                        }

                        HttpTransportMetrics.DuplexResponseMessages.Add(1, metricTags);
                    }

                    var finalMeta = NormalizeResponseMeta(streamCall.ResponseMeta, transport);
                    var completePayload = HttpDuplexProtocol.SerializeResponseMeta(finalMeta);
                    var completeResult = await SendWebSocketFrameAsync(
                            webSocket,
                            HttpDuplexProtocol.FrameType.ResponseComplete,
                            completePayload,
                            writeTimeout,
                            transport,
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    if (completeResult.IsFailure)
                    {
                        var completionError = NormalizeDuplexError(completeResult.Error, transport);
                        await streamCall.CompleteResponsesAsync(completionError, CancellationToken.None).ConfigureAwait(false);
                        await streamCall.CompleteRequestsAsync(completionError, CancellationToken.None).ConfigureAwait(false);
                        pumpCts.Cancel();
                    }
                }
                catch (OperationCanceledException)
                {
                    var error = OmniRelayErrorAdapter.FromStatus(
                        OmniRelayStatusCode.Cancelled,
                        "The client cancelled the response stream.",
                        transport: transport);
                    await HttpDuplexProtocol.SendFrameResultAsync(
                            webSocket,
                            HttpDuplexProtocol.FrameType.ResponseError,
                            HttpDuplexProtocol.CreateErrorPayload(error),
                            transport,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await streamCall.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
                    pumpCts.Cancel();
                }
                catch (Exception ex)
                {
                    var error = NormalizeDuplexError(OmniRelayErrors.FromException(ex, transport).Error, transport);
                    await HttpDuplexProtocol.SendFrameResultAsync(
                            webSocket,
                            HttpDuplexProtocol.FrameType.ResponseError,
                            HttpDuplexProtocol.CreateErrorPayload(error),
                            transport,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await streamCall.CompleteResponsesAsync(error, CancellationToken.None).ConfigureAwait(false);
                    pumpCts.Cancel();
                }
            }
        }
        finally
        {
            CompleteRequest(context);
        }
    }

    private static async ValueTask WritePipeAsync(PipeWriter writer, ReadOnlyMemory<byte> payload, TimeSpan? timeout, CancellationToken baseToken)
    {
        CancellationTokenSource? timeoutCts = null;
        var token = baseToken;

        if (timeout.HasValue)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(baseToken);
            timeoutCts.CancelAfter(timeout.Value);
            token = timeoutCts.Token;
        }

        try
        {
            var result = await writer.WriteAsync(payload, token).ConfigureAwait(false);
            if (result.IsCanceled)
            {
                ThrowIfTimedOut(timeout, timeoutCts, baseToken);
                throw new OperationCanceledException(token);
            }
        }
        catch (OperationCanceledException)
        {
            ThrowIfTimedOut(timeout, timeoutCts, baseToken);
            throw;
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private static async ValueTask FlushPipeAsync(PipeWriter writer, TimeSpan? timeout, CancellationToken baseToken)
    {
        CancellationTokenSource? timeoutCts = null;
        var token = baseToken;

        if (timeout.HasValue)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(baseToken);
            timeoutCts.CancelAfter(timeout.Value);
            token = timeoutCts.Token;
        }

        try
        {
            var result = await writer.FlushAsync(token).ConfigureAwait(false);
            if (result.IsCanceled)
            {
                ThrowIfTimedOut(timeout, timeoutCts, baseToken);
                throw new OperationCanceledException(token);
            }
        }
        catch (OperationCanceledException)
        {
            ThrowIfTimedOut(timeout, timeoutCts, baseToken);
            throw;
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private static async ValueTask<Result<Unit>> SendWebSocketFrameAsync(
        WebSocket socket,
        HttpDuplexProtocol.FrameType frameType,
        ReadOnlyMemory<byte> payload,
        TimeSpan? timeout,
        string transport,
        CancellationToken baseToken)
    {
        CancellationTokenSource? timeoutCts = null;
        var token = baseToken;

        if (timeout.HasValue)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(baseToken);
            timeoutCts.CancelAfter(timeout.Value);
            token = timeoutCts.Token;
        }

        try
        {
            var sendResult = await HttpDuplexProtocol.SendFrameResultAsync(
                    socket,
                    frameType,
                    payload,
                    transport,
                    token)
                .ConfigureAwait(false);

            if (sendResult.IsFailure &&
                timeout.HasValue &&
                timeoutCts is { IsCancellationRequested: true } &&
                !baseToken.IsCancellationRequested)
            {
                return Err<Unit>(OmniRelayErrorAdapter.FromStatus(
                    OmniRelayStatusCode.DeadlineExceeded,
                    "The duplex response stream write timed out.",
                    transport));
            }

            return sendResult;
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private static void ThrowIfTimedOut(TimeSpan? timeout, CancellationTokenSource? timeoutCts, CancellationToken baseToken)
    {
        if (timeout.HasValue &&
            timeoutCts is not null &&
            timeoutCts.IsCancellationRequested &&
            !baseToken.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private static Error NormalizeDuplexError(Error? error, string transport) =>
        OmniRelayErrors.FromError(error ?? Error.Unspecified(), transport).Error;

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
        string protocol)
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

        var metaHeaders = new List<KeyValuePair<string, string>>(headers.Count + 1);
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, HttpTransportHeaders.Protocol, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            metaHeaders.Add(new KeyValuePair<string, string>(header.Key, header.Value.ToString()));
        }

        metaHeaders.Add(new KeyValuePair<string, string>(HttpTransportHeaders.Protocol, protocol));

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
        OmniRelayStatusCode status,
        string transport,
        Error? error = null)
    {
        if (context.Response.StatusCode == StatusCodes.Status200OK)
        {
            context.Response.StatusCode = HttpStatusMapper.ToStatusCode(status);
        }
        var statusText = status.ToString();
        var payloadStatus = FormatStatusToken(status);
        context.Response.Headers[HttpTransportHeaders.Transport] = transport;
        context.Response.Headers[HttpTransportHeaders.Protocol] = context.Request.Protocol;
        context.Response.Headers[HttpTransportHeaders.Status] = statusText;
        context.Response.Headers[HttpTransportHeaders.ErrorMessage] = message;
        if (error is not null && !string.IsNullOrEmpty(error.Code))
        {
            context.Response.Headers[HttpTransportHeaders.ErrorCode] = error.Code;
        }

        context.Response.ContentType = MediaTypeNames.Application.Json;

        Dictionary<string, string?>? metadata = null;
        if (error?.Metadata is { Count: > 0 })
        {
            metadata = error.Metadata.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString(), StringComparer.OrdinalIgnoreCase);
        }

        var payload = new ErrorPayload(message, payloadStatus, error?.Code, metadata);

        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                payload,
                JsonContext.ErrorPayload,
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static byte[] CopyFramePayload(ReadOnlyMemory<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        var copy = GC.AllocateUninitializedArray<byte>(payload.Length);
        payload.Span.CopyTo(copy);
        return copy;
    }

    private static string FormatStatusToken(OmniRelayStatusCode status)
    {
        var text = status.ToString();
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length * 2);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsUpper(ch) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToUpperInvariant(ch));
        }

        return builder.ToString();
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

    private sealed record HealthPayload(string Status, string Mode, string[] Issues, int ActiveRequests, bool Draining);

    private sealed record ErrorPayload(string Message, string Status, string? Code, IDictionary<string, string?>? Metadata);

    [JsonSourceGenerationOptions(
        JsonSerializerDefaults.Web,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UseStringEnumConverter = true,
        WriteIndented = true)]
    [JsonSerializable(typeof(DispatcherIntrospection))]
    [JsonSerializable(typeof(DeploymentMode))]
    [JsonSerializable(typeof(HealthPayload))]
    [JsonSerializable(typeof(ErrorPayload))]
    private sealed partial class HttpInboundJsonContext : JsonSerializerContext;

}
