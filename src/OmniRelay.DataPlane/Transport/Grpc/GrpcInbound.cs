using System.Net;
using System.Security.Authentication;
using Grpc.AspNetCore.Server.Model;
using Grpc.Core;
using Hugo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Quic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OmniRelay.ControlPlane.Upgrade;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Authorization;
using OmniRelay.Transport.Grpc.Interceptors;
using OmniRelay.Transport.Http;
using OmniRelay.Transport.Security;
using static Hugo.Go;

namespace OmniRelay.Transport.Grpc;

/// <summary>
/// Hosts the OmniRelay gRPC inbound service that dispatches arbitrary procedures via a single gRPC service.
/// Supports HTTP/2 and can enable HTTP/3 (QUIC) when configured with TLS 1.3.
/// </summary>
public sealed partial class GrpcInbound : ILifecycle, IDispatcherAware, IGrpcServerInterceptorSink, INodeDrainParticipant
{
    private static readonly Error UrlsRequired = Error.From("At least one URL must be provided for the gRPC inbound.", "grpc.inbound.urls_missing");
    private readonly Uri[] _urls;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly Action<WebApplication>? _configureApp;
    private WebApplication? _app;
    private Dispatcher.Dispatcher? _dispatcher;
    private readonly GrpcServerTlsOptions? _serverTlsOptions;
    private readonly GrpcCompressionOptions? _compressionOptions;
    private readonly GrpcTelemetryOptions? _telemetryOptions;
    private readonly TransportSecurityPolicyEvaluator? _transportSecurity;
    private readonly IMeshAuthorizationEvaluator? _authorizationEvaluator;
    private GrpcServerInterceptorRegistry? _serverInterceptorRegistry;
    private int _interceptorsConfigured;
    private volatile bool _isDraining;
    private readonly WaitGroup _activeCalls = new();
    private readonly HashSet<ServerCallContext> _activeCallContexts = [];
    private readonly object _callContextLock = new();
    private const string RetryAfterMetadataName = "retry-after";
    private const string RetryAfterMetadataValue = "1";

    /// <summary>
    /// Creates a new gRPC inbound server with optional DI and app configuration hooks.
    /// </summary>
    /// <param name="urls">The URLs to bind (https required for HTTP/3).</param>
    /// <param name="configureServices">Optional service collection configuration.</param>
    /// <param name="configureApp">Optional application pipeline configuration.</param>
    /// <param name="serverTlsOptions">TLS options including certificate for HTTPS/HTTP/3.</param>
    /// <param name="serverRuntimeOptions">gRPC server runtime options and HTTP/3 settings.</param>
    /// <param name="compressionOptions">Optional compression providers and defaults.</param>
    /// <param name="telemetryOptions">Optional telemetry options such as logging toggles.</param>
    /// <param name="transportSecurity"></param>
    /// <param name="authorizationEvaluator"></param>
    public static Result<GrpcInbound> TryCreate(
        IEnumerable<Uri> urls,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null,
        GrpcServerTlsOptions? serverTlsOptions = null,
        GrpcServerRuntimeOptions? serverRuntimeOptions = null,
        GrpcCompressionOptions? compressionOptions = null,
        GrpcTelemetryOptions? telemetryOptions = null,
        TransportSecurityPolicyEvaluator? transportSecurity = null,
        IMeshAuthorizationEvaluator? authorizationEvaluator = null)
    {
        if (urls is null)
        {
            return Err<GrpcInbound>(UrlsRequired);
        }

        return TryCreate(
            urls.Select(static uri => uri?.ToString() ?? string.Empty),
            configureServices,
            configureApp,
            serverTlsOptions,
            serverRuntimeOptions,
            compressionOptions,
            telemetryOptions,
            transportSecurity,
            authorizationEvaluator);
    }

    /// <summary>
    /// Creates a new gRPC inbound server with optional DI and app configuration hooks.
    /// </summary>
    /// <param name="urls">The URLs to bind (https required for HTTP/3).</param>
    /// <param name="configureServices">Optional service collection configuration.</param>
    /// <param name="configureApp">Optional application pipeline configuration.</param>
    /// <param name="serverTlsOptions">TLS options including certificate for HTTPS/HTTP/3.</param>
    /// <param name="serverRuntimeOptions">gRPC server runtime options and HTTP/3 settings.</param>
    /// <param name="compressionOptions">Optional compression providers and defaults.</param>
    /// <param name="telemetryOptions">Optional telemetry options such as logging toggles.</param>
    /// <param name="transportSecurity"></param>
    /// <param name="authorizationEvaluator"></param>
    public static Result<GrpcInbound> TryCreate(
        IEnumerable<string> urls,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null,
        GrpcServerTlsOptions? serverTlsOptions = null,
        GrpcServerRuntimeOptions? serverRuntimeOptions = null,
        GrpcCompressionOptions? compressionOptions = null,
        GrpcTelemetryOptions? telemetryOptions = null,
        TransportSecurityPolicyEvaluator? transportSecurity = null,
        IMeshAuthorizationEvaluator? authorizationEvaluator = null)
    {
        if (urls is null)
        {
            return Err<GrpcInbound>(UrlsRequired);
        }

        var list = urls.ToArray();
        if (list.Length == 0)
        {
            return Err<GrpcInbound>(UrlsRequired);
        }

        try
        {
            return Ok(new GrpcInbound(
                list.Select(u => new Uri(u, UriKind.Absolute)),
                configureServices,
                configureApp,
                serverTlsOptions,
                serverRuntimeOptions,
                compressionOptions,
                telemetryOptions,
                transportSecurity,
                authorizationEvaluator));
        }
        catch (Exception ex)
        {
            return Err<GrpcInbound>(Error.FromException(ex));
        }
    }

    public GrpcInbound(
        IEnumerable<string> urls,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null,
        GrpcServerTlsOptions? serverTlsOptions = null,
        GrpcServerRuntimeOptions? serverRuntimeOptions = null,
        GrpcCompressionOptions? compressionOptions = null,
        GrpcTelemetryOptions? telemetryOptions = null,
        TransportSecurityPolicyEvaluator? transportSecurity = null,
        IMeshAuthorizationEvaluator? authorizationEvaluator = null)
        : this(urls.Select(u => new Uri(u, UriKind.Absolute)), configureServices, configureApp, serverTlsOptions, serverRuntimeOptions, compressionOptions, telemetryOptions, transportSecurity, authorizationEvaluator)
    {
    }

    public GrpcInbound(
        IEnumerable<Uri> urls,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null,
        GrpcServerTlsOptions? serverTlsOptions = null,
        GrpcServerRuntimeOptions? serverRuntimeOptions = null,
        GrpcCompressionOptions? compressionOptions = null,
        GrpcTelemetryOptions? telemetryOptions = null,
        TransportSecurityPolicyEvaluator? transportSecurity = null,
        IMeshAuthorizationEvaluator? authorizationEvaluator = null)
    {
        _urls = urls?.Select(u => u ?? throw new ArgumentException("Inbound URL cannot be null.", nameof(urls))).ToArray()
            ?? throw new ArgumentNullException(nameof(urls));
        if (_urls.Length == 0)
        {
            throw new ArgumentException("At least one URL must be provided for the gRPC inbound.", nameof(urls));
        }

        _configureServices = configureServices;
        _configureApp = configureApp;
        _serverTlsOptions = serverTlsOptions;
        RuntimeOptions = serverRuntimeOptions;
        _compressionOptions = compressionOptions;
        _telemetryOptions = telemetryOptions;
        _transportSecurity = transportSecurity;
        _authorizationEvaluator = authorizationEvaluator;
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
    /// Starts the gRPC server and begins accepting calls.
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
            throw new InvalidOperationException("Dispatcher must be bound before starting the gRPC inbound.");
        }

        var builder = WebApplication.CreateSlimBuilder();
        var enableHttp3 = RuntimeOptions?.EnableHttp3 == true;
        var http3Endpoints = enableHttp3 ? new List<string>() : null;
        var http3RuntimeOptions = RuntimeOptions?.Http3;
        var streamLimitUnsupported = false;
        var idleTimeoutUnsupported = false;
        var keepAliveUnsupported = false;

        var http3Validation = ValidateHttp3Options(enableHttp3, http3RuntimeOptions);
        if (http3Validation.IsFailure)
        {
            throw new InvalidOperationException(http3Validation.Error?.Message ?? "Invalid HTTP/3 configuration for gRPC inbound.");
        }

        if (enableHttp3)
        {
            builder.WebHost.UseQuic(quicOptions =>
            {
                if (http3RuntimeOptions?.IdleTimeout is { } idleTimeout)
                {
                    if (!TrySetQuicOption(quicOptions, "IdleTimeout", idleTimeout))
                    {
                        idleTimeoutUnsupported = true;
                    }
                }

                if (http3RuntimeOptions?.KeepAliveInterval is { } keepAliveInterval)
                {
                    if (!TrySetQuicOption(quicOptions, "KeepAliveInterval", keepAliveInterval))
                    {
                        keepAliveUnsupported = true;
                    }
                }

                if (http3RuntimeOptions?.MaxBidirectionalStreams is { } maxBidirectionalStreams)
                {
                    if (!TrySetQuicOption(quicOptions, "MaxBidirectionalStreamCount", maxBidirectionalStreams))
                    {
                        streamLimitUnsupported = true;
                    }
                }

                if (http3RuntimeOptions?.MaxUnidirectionalStreams is { } maxUnidirectionalStreams)
                {
                    if (!TrySetQuicOption(quicOptions, "MaxUnidirectionalStreamCount", maxUnidirectionalStreams))
                    {
                        streamLimitUnsupported = true;
                    }
                }
            });
        }

        builder.WebHost.UseKestrel(options =>
        {
            if (RuntimeOptions != null)
            {
                if (RuntimeOptions.KeepAlivePingDelay is { } delay)
                {
                    options.Limits.Http2.KeepAlivePingDelay = delay;
                }

                if (RuntimeOptions.KeepAlivePingTimeout is { } timeout)
                {
                    options.Limits.Http2.KeepAlivePingTimeout = timeout;
                }
            }

            foreach (var uri in _urls)
            {
                var host = string.Equals(uri.Host, "*", StringComparison.Ordinal) ? IPAddress.Any : IPAddress.Parse(uri.Host);
                options.Listen(host, uri.Port, listenOptions =>
                {
                    if (enableHttp3)
                    {
                        listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;

                        var endpointCheck = ValidateHttp3Endpoint(uri.ToString());
                        if (endpointCheck.IsFailure)
                        {
                            throw new InvalidOperationException(endpointCheck.Error?.Message ?? "Invalid HTTP/3 endpoint configuration.");
                        }

                        Http3RuntimeGuards.EnsureServerSupport(uri.ToString(), _serverTlsOptions?.Certificate);

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
                        listenOptions.Protocols = HttpProtocols.Http2;
                    }

                    if (_serverTlsOptions != null)
                    {
                        var enabledProtocols = _serverTlsOptions.EnabledProtocols;
                        if (enableHttp3 && enabledProtocols is { } specifiedProtocols && (specifiedProtocols & SslProtocols.Tls13) == 0)
                        {
                            throw new InvalidOperationException($"HTTP/3 requires TLS 1.3 but the configured protocol set for '{uri}' excludes it.");
                        }

                        var httpsOptions = new HttpsConnectionAdapterOptions
                        {
                            ServerCertificate = _serverTlsOptions.Certificate,
                            ClientCertificateMode = _serverTlsOptions.ClientCertificateMode
                        };

                        if (enableHttp3)
                        {
                            httpsOptions.SslProtocols = enabledProtocols ?? (SslProtocols.Tls12 | SslProtocols.Tls13);
                        }
                        else if (enabledProtocols is not null)
                        {
                            httpsOptions.SslProtocols = enabledProtocols.Value;
                        }

                        if (_serverTlsOptions.CheckCertificateRevocation is { } checkRevocation)
                        {
                            httpsOptions.CheckCertificateRevocation = checkRevocation;
                        }

                        if (_serverTlsOptions.ClientCertificateValidation is { } validator)
                        {
                            httpsOptions.ClientCertificateValidation = (certificate, chain, errors) =>
                                validator(certificate, chain, errors);
                        }

                        listenOptions.UseHttps(httpsOptions);
                    }
                    else if (enableHttp3)
                    {
                        throw new InvalidOperationException($"HTTP/3 requires HTTPS. Configure TLS for '{uri}' or disable HTTP/3 for this listener.");
                    }
                });
            }
        });

        builder.Services.AddSingleton(_dispatcher);
        builder.Services.AddSingleton<IServiceMethodProvider<GrpcDispatcherService>>(
            _ => new GrpcDispatcherServiceMethodProvider(_dispatcher, this));
        builder.Services.AddSingleton<GrpcDispatcherService>();

        var runtimeLoggingInterceptorSpecified = RuntimeOptions?.Interceptors?.Any(type => type == typeof(GrpcServerLoggingInterceptor)) == true;
        if (runtimeLoggingInterceptorSpecified || (_telemetryOptions?.EnableServerLogging == true))
        {
            builder.Services.TryAddSingleton<GrpcServerLoggingInterceptor>();
        }

        var hasServerTransportInterceptors = _serverInterceptorRegistry is not null;

        if (hasServerTransportInterceptors)
        {
            builder.Services.AddSingleton(new CompositeServerInterceptor(_serverInterceptorRegistry!));
        }

        builder.Services.AddSingleton(new GrpcTransportHealthService(_dispatcher, this));

        if (_transportSecurity is not null)
        {
            builder.Services.AddSingleton(_transportSecurity);
            builder.Services.AddSingleton<TransportSecurityGrpcInterceptor>();
        }

        builder.Services.AddGrpc(options =>
        {
            var loggingInterceptorAdded = false;

            if (hasServerTransportInterceptors)
            {
                options.Interceptors.Add<CompositeServerInterceptor>();
            }

            if (RuntimeOptions != null)
            {
                if (RuntimeOptions.MaxReceiveMessageSize is { } maxReceive)
                {
                    options.MaxReceiveMessageSize = maxReceive;
                }

                if (RuntimeOptions.MaxSendMessageSize is { } maxSend)
                {
                    options.MaxSendMessageSize = maxSend;
                }

                if (RuntimeOptions.EnableDetailedErrors is { } detailedErrors)
                {
                    options.EnableDetailedErrors = detailedErrors;
                }

                if (RuntimeOptions?.AnnotatedInterceptors is { Count: > 0 } interceptors)
                {
                    foreach (var interceptor in interceptors)
                    {
                        var interceptorType = interceptor.Type;

                        if (interceptorType == typeof(GrpcServerLoggingInterceptor))
                        {
                            loggingInterceptorAdded = true;
                        }

                        options.Interceptors.Add(interceptorType);
                    }
                }
            }

            if (_telemetryOptions?.EnableServerLogging == true && !loggingInterceptorAdded)
            {
                options.Interceptors.Add<GrpcServerLoggingInterceptor>();
            }

            if (_transportSecurity is not null)
            {
                options.Interceptors.Add<TransportSecurityGrpcInterceptor>();
            }

            if (_compressionOptions != null)
            {
                _compressionOptions.Validate();

                if (_compressionOptions.Providers.Count > 0)
                {
                    options.CompressionProviders.Clear();
                    foreach (var provider in _compressionOptions.Providers)
                    {
                        options.CompressionProviders.Add(provider);
                    }
                }

                if (!string.IsNullOrWhiteSpace(_compressionOptions.DefaultAlgorithm))
                {
                    options.ResponseCompressionAlgorithm = _compressionOptions.DefaultAlgorithm;
                }

                if (_compressionOptions.DefaultCompressionLevel is { } compressionLevel)
                {
                    options.ResponseCompressionLevel = compressionLevel;
                }
            }
        });

        _configureServices?.Invoke(builder.Services);

        var app = builder.Build();

        if (enableHttp3)
        {
            if (http3Endpoints is { Count: > 0 })
            {
                GrpcInboundLog.Http3Enabled(app.Logger, http3Endpoints.Count, string.Join(", ", http3Endpoints));
            }
            else
            {
                GrpcInboundLog.Http3MissingHttps(app.Logger);
            }

            if (streamLimitUnsupported)
            {
                GrpcInboundLog.Http3StreamLimitUnsupported(app.Logger);
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

                GrpcInboundLog.Http3OptionsUnsupported(app.Logger, string.Join(" and ", unsupportedOptions));
            }
        }

        _configureApp?.Invoke(app);

        app.MapGrpcService<GrpcDispatcherService>();
        app.MapGrpcService<GrpcTransportHealthService>();
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
    }

    void IGrpcServerInterceptorSink.AttachGrpcServerInterceptors(GrpcServerInterceptorRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (Interlocked.Exchange(ref _interceptorsConfigured, 1) == 1)
        {
            return;
        }

        _serverInterceptorRegistry = registry;
    }

    internal bool IsDraining => _isDraining;

    internal GrpcServerRuntimeOptions? RuntimeOptions { get; }

    internal bool TryEnterCall(ServerCallContext context, out IDisposable? scope, out RpcException? rejection)
    {
        if (_isDraining)
        {
            rejection = CreateShutdownException();
            scope = null;
            return false;
        }

        _activeCalls.Add(1);
        TrackCall(context);
        scope = new CallScope(this, context);
        rejection = null;
        return true;
    }

    private void TrackCall(ServerCallContext context)
    {
        lock (_callContextLock)
        {
            _activeCallContexts.Add(context);
        }
    }

    private void OnCallCompleted(ServerCallContext context)
    {
        lock (_callContextLock)
        {
            _activeCallContexts.Remove(context);
        }

        _activeCalls.Done();
    }

    private void AbortActiveCalls()
    {
        List<ServerCallContext> contexts;
        lock (_callContextLock)
        {
            if (_activeCallContexts.Count == 0)
            {
                return;
            }

            contexts = [.. _activeCallContexts];
        }

        foreach (var call in contexts)
        {
            try
            {
                var httpContext = call.GetHttpContext();
                httpContext?.Abort();
            }
            catch
            {
                // ignore abort failures
            }
        }
    }

    private async ValueTask<bool> WaitForDrainAsync(bool swallowCancellation, CancellationToken cancellationToken)
    {
        if (_app is null)
        {
            return false;
        }

        if (!_isDraining)
        {
            _isDraining = true;
        }

        try
        {
            await _activeCalls.WaitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            if (swallowCancellation)
            {
                return true;
            }

            throw;
        }
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

    private Result<Unit> ValidateHttp3Options(bool enableHttp3, Http3RuntimeOptions? runtime)
    {
        if (!enableHttp3)
        {
            return Ok(Unit.Value);
        }

        if (_serverTlsOptions?.Certificate is null)
        {
            return Err<Unit>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.InvalidArgument,
                "HTTP/3 requires TLS. Configure a server certificate for the gRPC inbound or disable HTTP/3.",
                transport: GrpcTransportConstants.TransportName));
        }

        if (runtime?.IdleTimeout is { } idle && idle <= TimeSpan.Zero)
        {
            return Err<Unit>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.InvalidArgument,
                "HTTP/3 IdleTimeout must be greater than zero for the gRPC inbound.",
                transport: GrpcTransportConstants.TransportName));
        }

        if (runtime?.KeepAliveInterval is { } keepAlive && keepAlive <= TimeSpan.Zero)
        {
            return Err<Unit>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.InvalidArgument,
                "HTTP/3 KeepAliveInterval must be greater than zero for the gRPC inbound.",
                transport: GrpcTransportConstants.TransportName));
        }

        if (runtime?.MaxBidirectionalStreams is { } maxBi && maxBi <= 0)
        {
            return Err<Unit>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.InvalidArgument,
                "HTTP/3 MaxBidirectionalStreams must be greater than zero for the gRPC inbound.",
                transport: GrpcTransportConstants.TransportName));
        }

        if (runtime?.MaxUnidirectionalStreams is { } maxUni && maxUni <= 0)
        {
            return Err<Unit>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.InvalidArgument,
                "HTTP/3 MaxUnidirectionalStreams must be greater than zero for the gRPC inbound.",
                transport: GrpcTransportConstants.TransportName));
        }

        return Ok(Unit.Value);
    }

    private static Result<Unit> ValidateHttp3Endpoint(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Err<Unit>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.InvalidArgument,
                "HTTP/3 endpoint URL was empty.",
                transport: GrpcTransportConstants.TransportName));
        }

        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Err<Unit>(OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.InvalidArgument,
                $"HTTP/3 requires HTTPS. Update inbound URL '{url}' to use https:// or disable HTTP/3 for this listener.",
                transport: GrpcTransportConstants.TransportName));
        }

        return Ok(Unit.Value);
    }

    private static RpcException CreateShutdownException()
    {
        var metadata = new Metadata
        {
            { RetryAfterMetadataName, RetryAfterMetadataValue }
        };

        return new RpcException(new Status(StatusCode.Unavailable, "server shutting down"), metadata);
    }

    private sealed class CallScope(GrpcInbound owner, ServerCallContext context) : IDisposable
    {
        private readonly GrpcInbound _owner = owner;
        private readonly ServerCallContext _context = context;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.OnCallCompleted(_context);
            }
        }
    }

    /// <summary>
    /// Initiates graceful drain and stops the gRPC server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null)
        {
            return;
        }

        var cancellationRequested = await WaitForDrainAsync(swallowCancellation: true, cancellationToken).ConfigureAwait(false);

        cancellationRequested |= cancellationToken.IsCancellationRequested;
        var stopToken = cancellationRequested ? CancellationToken.None : cancellationToken;

        if (cancellationRequested)
        {
            AbortActiveCalls();
        }

        try
        {
            await _app.StopAsync(stopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation during server stop so shutdown continues.
        }

        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
        _isDraining = false;
    }

    async ValueTask INodeDrainParticipant.DrainAsync(CancellationToken cancellationToken)
    {
        await WaitForDrainAsync(swallowCancellation: false, cancellationToken).ConfigureAwait(false);
    }

    ValueTask INodeDrainParticipant.ResumeAsync(CancellationToken cancellationToken)
    {
        _isDraining = false;
        return ValueTask.CompletedTask;
    }

    private static partial class GrpcInboundLog
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "gRPC HTTP/3 enabled on {EndpointCount} endpoint(s): {Endpoints}")]
        public static partial void Http3Enabled(ILogger logger, int endpointCount, string endpoints);

        [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "gRPC HTTP/3 was requested but no HTTPS endpoints were configured; falling back to HTTP/2.")]
        public static partial void Http3MissingHttps(ILogger logger);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "gRPC HTTP/3 stream limit tuning is not supported by the current MsQuic transport; configured values will be ignored.")]
        public static partial void Http3StreamLimitUnsupported(ILogger logger);

        [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "HTTP/3 {Options} tuning is not supported by the current MsQuic transport; configured values will be ignored.")]
        public static partial void Http3OptionsUnsupported(ILogger logger, string options);
    }

}
