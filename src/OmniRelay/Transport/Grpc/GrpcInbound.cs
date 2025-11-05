using System.Collections.Generic;
using System.Net;
using Grpc.AspNetCore.Server.Model;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Grpc.Interceptors;

namespace OmniRelay.Transport.Grpc;

public sealed class GrpcInbound : ILifecycle, IDispatcherAware, IGrpcServerInterceptorSink
{
    private readonly string[] _urls;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly Action<WebApplication>? _configureApp;
    private WebApplication? _app;
    private Dispatcher.Dispatcher? _dispatcher;
    private readonly GrpcServerTlsOptions? _serverTlsOptions;
    private readonly GrpcServerRuntimeOptions? _serverRuntimeOptions;
    private readonly GrpcCompressionOptions? _compressionOptions;
    private readonly GrpcTelemetryOptions? _telemetryOptions;
    private GrpcServerInterceptorRegistry? _serverInterceptorRegistry;
    private int _interceptorsConfigured;
    private volatile bool _isDraining;
    private int _activeCalls;
    private TaskCompletionSource<bool> _drainCompletion = CreateDrainTcs();
    private const string RetryAfterMetadataName = "retry-after";
    private const string RetryAfterMetadataValue = "1";

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
        _serverRuntimeOptions = serverRuntimeOptions;
        _compressionOptions = compressionOptions;
        _telemetryOptions = telemetryOptions;
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
            throw new InvalidOperationException("Dispatcher must be bound before starting the gRPC inbound.");
        }

    var builder = WebApplication.CreateSlimBuilder();
    var enableHttp3 = _serverRuntimeOptions?.EnableHttp3 == true;
    var http3Endpoints = enableHttp3 ? new List<string>() : null;

    builder.WebHost.UseKestrel(options =>
        {
            if (_serverRuntimeOptions is { } runtimeOptions)
            {
                if (runtimeOptions.KeepAlivePingDelay is { } delay)
                {
                    options.Limits.Http2.KeepAlivePingDelay = delay;
                }

                if (runtimeOptions.KeepAlivePingTimeout is { } timeout)
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
                        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"HTTP/3 requires HTTPS. Update inbound URL '{url}' to use https:// or disable HTTP/3 for this listener.");
                        }

                        listenOptions.Protocols = HttpProtocols.Http2 | HttpProtocols.Http3;
                        http3Endpoints?.Add(url);
                    }
                    else
                    {
                        listenOptions.Protocols = HttpProtocols.Http2;
                    }

                    if (_serverTlsOptions is { } tlsOptions)
                    {
                        var httpsOptions = new HttpsConnectionAdapterOptions
                        {
                            ServerCertificate = tlsOptions.Certificate,
                            ClientCertificateMode = tlsOptions.ClientCertificateMode
                        };

                        if (tlsOptions.EnabledProtocols is not null)
                        {
                            httpsOptions.SslProtocols = tlsOptions.EnabledProtocols.Value;
                        }

                        if (tlsOptions.CheckCertificateRevocation is { } checkRevocation)
                        {
                            httpsOptions.CheckCertificateRevocation = checkRevocation;
                        }

                        if (tlsOptions.ClientCertificateValidation is { } validator)
                        {
                            httpsOptions.ClientCertificateValidation = (certificate, chain, errors) =>
                                validator(certificate, chain, errors);
                        }

                        listenOptions.UseHttps(httpsOptions);
                    }
                });
            }
    });

        builder.Services.AddSingleton(_dispatcher);
        builder.Services.AddSingleton<IServiceMethodProvider<GrpcDispatcherService>>(
            _ => new GrpcDispatcherServiceMethodProvider(_dispatcher, this));
        builder.Services.AddSingleton<GrpcDispatcherService>();

        var runtimeLoggingInterceptorSpecified = _serverRuntimeOptions?.Interceptors?.Any(type => type == typeof(GrpcServerLoggingInterceptor)) == true;
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

            if (_serverRuntimeOptions is { } runtimeOptions)
            {
                if (runtimeOptions.MaxReceiveMessageSize is { } maxReceive)
                {
                    options.MaxReceiveMessageSize = maxReceive;
                }

                if (runtimeOptions.MaxSendMessageSize is { } maxSend)
                {
                    options.MaxSendMessageSize = maxSend;
                }

                if (runtimeOptions.EnableDetailedErrors is { } detailedErrors)
                {
                    options.EnableDetailedErrors = detailedErrors;
                }

                if (runtimeOptions.Interceptors is { Count: > 0 } interceptors)
                {
                    foreach (var interceptorType in interceptors)
                    {
                        if (interceptorType is null)
                        {
                            continue;
                        }

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

            if (_compressionOptions is { } compressionOptions)
            {
                compressionOptions.Validate();

                if (compressionOptions.Providers.Count > 0)
                {
                    options.CompressionProviders.Clear();
                    foreach (var provider in compressionOptions.Providers)
                    {
                        options.CompressionProviders.Add(provider);
                    }
                }

                if (!string.IsNullOrWhiteSpace(compressionOptions.DefaultAlgorithm))
                {
                    options.ResponseCompressionAlgorithm = compressionOptions.DefaultAlgorithm;
                }

                if (compressionOptions.DefaultCompressionLevel is { } compressionLevel)
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

    internal GrpcServerRuntimeOptions? RuntimeOptions => _serverRuntimeOptions;

    internal bool TryEnterCall(ServerCallContext context, out IDisposable? scope, out RpcException? rejection)
    {
        if (_isDraining)
        {
            rejection = CreateShutdownException();
            scope = null;
            return false;
        }

        Interlocked.Increment(ref _activeCalls);
        scope = new CallScope(this);
        rejection = null;
        return true;
    }

    private void OnCallCompleted()
    {
        if (Interlocked.Decrement(ref _activeCalls) == 0 && _isDraining)
        {
            _drainCompletion.TrySetResult(true);
        }
    }

    private static TaskCompletionSource<bool> CreateDrainTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static RpcException CreateShutdownException()
    {
        var metadata = new Metadata
        {
            { RetryAfterMetadataName, RetryAfterMetadataValue }
        };

        return new RpcException(new Status(StatusCode.Unavailable, "server shutting down"), metadata);
    }

    private sealed class CallScope(GrpcInbound owner) : IDisposable
    {
        private readonly GrpcInbound _owner = owner;
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.OnCallCompleted();
            }
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null)
        {
            return;
        }

        _isDraining = true;

        if (Volatile.Read(ref _activeCalls) == 0)
        {
            _drainCompletion.TrySetResult(true);
        }

        try
        {
            await _drainCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Allow fast shutdown when cancellation is requested.
        }

        try
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation during server stop so shutdown continues.
        }

        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
        _isDraining = false;
        Interlocked.Exchange(ref _activeCalls, 0);
        _drainCompletion = CreateDrainTcs();
    }
}
