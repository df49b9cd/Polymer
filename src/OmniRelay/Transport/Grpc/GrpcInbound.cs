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
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Grpc.Interceptors;
using OmniRelay.Transport.Http;

namespace OmniRelay.Transport.Grpc;

/// <summary>
/// Hosts the OmniRelay gRPC inbound service that dispatches arbitrary procedures via a single gRPC service.
/// Supports HTTP/2 and can enable HTTP/3 (QUIC) when configured with TLS 1.3.
/// </summary>
public sealed class GrpcInbound : ILifecycle, IDispatcherAware, IGrpcServerInterceptorSink
{
    private readonly string[] _urls;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly Action<WebApplication>? _configureApp;
    private WebApplication? _app;
    private Dispatcher.Dispatcher? _dispatcher;
    private readonly GrpcServerTlsOptions? _serverTlsOptions;
    private readonly GrpcCompressionOptions? _compressionOptions;
    private readonly GrpcTelemetryOptions? _telemetryOptions;
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
    public GrpcInbound(
        IEnumerable<string> urls,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null,
        GrpcServerTlsOptions? serverTlsOptions = null,
        GrpcServerRuntimeOptions? serverRuntimeOptions = null,
        GrpcCompressionOptions? compressionOptions = null,
        GrpcTelemetryOptions? telemetryOptions = null)
    {
        _urls = urls?.ToArray() ?? throw new ArgumentNullException(nameof(urls));
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

        if (enableHttp3 && _serverTlsOptions?.Certificate is null)
        {
            throw new InvalidOperationException("HTTP/3 requires TLS. Configure a server certificate for the gRPC inbound or disable HTTP/3.");
        }

        if (enableHttp3)
        {
            builder.WebHost.UseQuic(quicOptions =>
            {
                if (http3RuntimeOptions?.IdleTimeout is { } idleTimeout)
                {
                    if (idleTimeout <= TimeSpan.Zero)
                    {
                        throw new InvalidOperationException("HTTP/3 IdleTimeout must be greater than zero for the gRPC inbound.");
                    }

                    if (!TrySetQuicOption(quicOptions, "IdleTimeout", idleTimeout))
                    {
                        idleTimeoutUnsupported = true;
                    }
                }

                if (http3RuntimeOptions?.KeepAliveInterval is { } keepAliveInterval)
                {
                    if (keepAliveInterval <= TimeSpan.Zero)
                    {
                        throw new InvalidOperationException("HTTP/3 KeepAliveInterval must be greater than zero for the gRPC inbound.");
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
                        throw new InvalidOperationException("HTTP/3 MaxBidirectionalStreams must be greater than zero for the gRPC inbound.");
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
                        throw new InvalidOperationException("HTTP/3 MaxUnidirectionalStreams must be greater than zero for the gRPC inbound.");
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

            foreach (var url in _urls)
            {
                var uri = new Uri(url, UriKind.Absolute);
                var host = string.Equals(uri.Host, "*", StringComparison.Ordinal) ? IPAddress.Any : IPAddress.Parse(uri.Host);
                options.Listen(host, uri.Port, listenOptions =>
                {
                    if (enableHttp3)
                    {
                        listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;

                        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"HTTP/3 requires HTTPS. Update inbound URL '{url}' to use https:// or disable HTTP/3 for this listener.");
                        }

                        Http3RuntimeGuards.EnsureServerSupport(url, _serverTlsOptions?.Certificate);

                        var enableAltSvc = http3RuntimeOptions?.EnableAltSvc;
                        listenOptions.DisableAltSvcHeader = enableAltSvc switch
                        {
                            true => false,
                            false => true,
                            _ => false
                        };

                        http3Endpoints?.Add(url);
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
                            throw new InvalidOperationException($"HTTP/3 requires TLS 1.3 but the configured protocol set for '{url}' excludes it.");
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
                        throw new InvalidOperationException($"HTTP/3 requires HTTPS. Configure TLS for '{url}' or disable HTTP/3 for this listener.");
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
                app.Logger.LogInformation("gRPC HTTP/3 enabled on {EndpointCount} endpoint(s): {Endpoints}", http3Endpoints.Count, string.Join(", ", http3Endpoints));
            }
            else
            {
                app.Logger.LogWarning("gRPC HTTP/3 was requested but no HTTPS endpoints were configured; falling back to HTTP/2.");
            }

            if (streamLimitUnsupported)
            {
                app.Logger.LogWarning("gRPC HTTP/3 stream limit tuning is not supported by the current MsQuic transport; configured values will be ignored.");
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

                app.Logger.LogWarning("HTTP/3 {Options} tuning is not supported by the current MsQuic transport; configured values will be ignored.", string.Join(" and ", unsupportedOptions));
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

            contexts = _activeCallContexts.ToList();
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

        _isDraining = true;
        var cancellationRequested = false;

        try
        {
            await _activeCalls.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Allow fast shutdown when cancellation is requested.
            cancellationRequested = true;
        }

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

}
