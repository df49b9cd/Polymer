using System.Net;
using System.Security.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OmniRelay.Transport.Grpc;

namespace OmniRelay.ControlPlane.Hosting;

/// <summary>
/// Builds gRPC control-plane hosts that match dispatcher runtime settings (HTTP/3, TLS, diagnostics hooks).
/// </summary>
public sealed class GrpcControlPlaneHostBuilder
{
    private readonly GrpcControlPlaneHostOptions _options;
    private readonly List<Action<IServiceCollection>> _serviceCallbacks = new();
    private readonly List<Action<WebApplication>> _appCallbacks = new();

    public GrpcControlPlaneHostBuilder(GrpcControlPlaneHostOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.Urls.Count == 0)
        {
            throw new ArgumentException("At least one gRPC endpoint must be configured.", nameof(options));
        }
    }

    public GrpcControlPlaneHostBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _serviceCallbacks.Add(configure);
        return this;
    }

    public GrpcControlPlaneHostBuilder ConfigureApp(Action<WebApplication> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _appCallbacks.Add(configure);
        return this;
    }

    /// <summary>Builds a <see cref="WebApplication"/> hosting gRPC control-plane services.</summary>
    public WebApplication Build()
    {
        var builder = WebApplication.CreateSlimBuilder();
        ConfigureKestrel(builder.WebHost);
        ConfigureGrpc(builder.Services);

        foreach (var callback in _serviceCallbacks)
        {
            callback(builder.Services);
        }

        var app = builder.Build();

        foreach (var callback in _appCallbacks)
        {
            callback(app);
        }

        return app;
    }

    private void ConfigureKestrel(IWebHostBuilder webHostBuilder)
    {
        var enableHttp3 = _options.Runtime?.EnableHttp3 ?? false;
        var tlsOptions = ResolveServerTlsOptions(enableHttp3);

        webHostBuilder.ConfigureKestrel(serverOptions =>
        {
            foreach (var url in _options.Urls)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    throw new InvalidOperationException($"Invalid gRPC endpoint '{url}'.");
                }

                Listen(serverOptions, uri, listenOptions =>
                {
                    listenOptions.Protocols = enableHttp3 ? HttpProtocols.Http3 : HttpProtocols.Http2;

                    if (tlsOptions is not null)
                    {
                        listenOptions.UseHttps(CreateHttpsOptions(tlsOptions, enableHttp3));
                    }
                    else if (enableHttp3)
                    {
                        throw new InvalidOperationException($"HTTP/3 requires HTTPS. Configure TLS for '{url}' or disable HTTP/3.");
                    }
                });
            }
        });
    }

    private GrpcServerTlsOptions? ResolveServerTlsOptions(bool enableHttp3)
    {
        if (_options.Tls is not null)
        {
            ValidateHttp3Protocols(enableHttp3, _options.Tls);
            return _options.Tls;
        }

        if (_options.SharedTls is null)
        {
            return null;
        }

        var certificate = _options.SharedTls.GetCertificate();
        var tls = new GrpcServerTlsOptions
        {
            Certificate = certificate,
            ClientCertificateMode = _options.ClientCertificateMode,
            CheckCertificateRevocation = _options.CheckCertificateRevocation,
            EnabledProtocols = enableHttp3 ? SslProtocols.Tls13 | SslProtocols.Tls12 : SslProtocols.Tls12 | SslProtocols.Tls13
        };

        return tls;
    }

    private static void ValidateHttp3Protocols(bool enableHttp3, GrpcServerTlsOptions tls)
    {
        if (!enableHttp3 || tls.EnabledProtocols is null)
        {
            return;
        }

        if ((tls.EnabledProtocols.Value & SslProtocols.Tls13) == 0)
        {
            throw new InvalidOperationException("HTTP/3 requires TLS 1.3 to be enabled on the control-plane host.");
        }
    }

    private static HttpsConnectionAdapterOptions CreateHttpsOptions(GrpcServerTlsOptions tls, bool enableHttp3)
    {
        var https = new HttpsConnectionAdapterOptions
        {
            ServerCertificate = tls.Certificate,
            ClientCertificateMode = tls.ClientCertificateMode,
            CheckCertificateRevocation = tls.CheckCertificateRevocation ?? true
        };

        if (enableHttp3)
        {
            https.SslProtocols = tls.EnabledProtocols ?? (SslProtocols.Tls12 | SslProtocols.Tls13);
        }
        else if (tls.EnabledProtocols is { } protocols)
        {
            https.SslProtocols = protocols;
        }

        if (tls.ClientCertificateValidation is not null)
        {
            https.ClientCertificateValidation = tls.ClientCertificateValidation;
        }

        return https;
    }

    private void ConfigureGrpc(IServiceCollection services)
    {
        var enableLogging = _options.Telemetry?.EnableServerLogging ?? false;
        if (enableLogging)
        {
            services.TryAddSingleton<GrpcServerLoggingInterceptor>();
        }

        services.AddGrpc(options =>
        {
            if (_options.Runtime is not null)
            {
                if (_options.Runtime.MaxReceiveMessageSize is { } maxReceive)
                {
                    options.MaxReceiveMessageSize = maxReceive;
                }

                if (_options.Runtime.MaxSendMessageSize is { } maxSend)
                {
                    options.MaxSendMessageSize = maxSend;
                }

                if (_options.Runtime.EnableDetailedErrors is { } detailedErrors)
                {
                    options.EnableDetailedErrors = detailedErrors;
                }

                foreach (var interceptor in _options.Runtime.AnnotatedInterceptors)
                {
                    options.Interceptors.Add(interceptor.Type);
                }
            }

            if (_options.Compression is not null)
            {
                var compression = _options.Compression;
                compression.Validate();

                if (compression.Providers.Count > 0)
                {
                    options.CompressionProviders.Clear();
                    foreach (var provider in compression.Providers)
                    {
                        options.CompressionProviders.Add(provider);
                    }
                }

                if (!string.IsNullOrWhiteSpace(compression.DefaultAlgorithm))
                {
                    options.ResponseCompressionAlgorithm = compression.DefaultAlgorithm;
                }

                if (compression.DefaultCompressionLevel is { } level)
                {
                    options.ResponseCompressionLevel = level;
                }
            }

            if (enableLogging)
            {
                options.Interceptors.Add<GrpcServerLoggingInterceptor>();
            }

            _options.ConfigureGrpc?.Invoke(options);
        });
    }

    private static void Listen(KestrelServerOptions serverOptions, Uri endpoint, Action<ListenOptions> configure)
    {
        if (string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            serverOptions.ListenLocalhost(endpoint.Port, configure);
            return;
        }

        if (IPAddress.TryParse(endpoint.Host, out var address))
        {
            serverOptions.Listen(address, endpoint.Port, configure);
            return;
        }

        if (endpoint.Host == "*" || endpoint.Host == "+")
        {
            serverOptions.ListenAnyIP(endpoint.Port, configure);
            return;
        }

        // Fallback to binding on all interfaces when a named host is used.
        serverOptions.ListenAnyIP(endpoint.Port, configure);
    }
}
