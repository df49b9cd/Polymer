using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Transport.Http;

namespace OmniRelay.ControlPlane.Hosting;

/// <summary>Builds HTTP/2 + HTTP/3 control-plane hosts that share dispatcher runtime defaults.</summary>
public sealed class HttpControlPlaneHostBuilder
{
    private readonly HttpControlPlaneHostOptions _options;
    private readonly List<Action<IServiceCollection>> _serviceCallbacks = new();
    private readonly List<Action<WebApplication>> _appCallbacks = new();

    public HttpControlPlaneHostBuilder(HttpControlPlaneHostOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.Urls.Count == 0)
        {
            throw new ArgumentException("At least one HTTP endpoint must be configured.", nameof(options));
        }
    }

    public HttpControlPlaneHostBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _serviceCallbacks.Add(configure);
        return this;
    }

    public HttpControlPlaneHostBuilder ConfigureApp(Action<WebApplication> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _appCallbacks.Add(configure);
        return this;
    }

    public WebApplication Build()
    {
        var builder = WebApplication.CreateSlimBuilder();
        ConfigureKestrel(builder.WebHost);

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
        var runtime = _options.Runtime;
        var enableHttp3 = runtime?.EnableHttp3 ?? false;
        var tlsOptions = ResolveTlsOptions(enableHttp3);

        webHostBuilder.ConfigureKestrel(serverOptions =>
        {
            ApplyRuntimeLimits(serverOptions, runtime);

            foreach (var url in _options.Urls)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    throw new InvalidOperationException($"Invalid HTTP endpoint '{url}'.");
                }

                Listen(serverOptions, uri, listenOptions =>
                {
                    listenOptions.Protocols = enableHttp3
                        ? HttpProtocols.Http1AndHttp2AndHttp3
                        : HttpProtocols.Http1AndHttp2;

                    if (tlsOptions is not null)
                    {
                        listenOptions.UseHttps(tlsOptions);
                    }
                    else if (enableHttp3)
                    {
                        throw new InvalidOperationException($"HTTP/3 requires HTTPS. Configure TLS for '{url}' or disable HTTP/3.");
                    }
                });
            }
        });
    }

    private HttpsConnectionAdapterOptions? ResolveTlsOptions(bool enableHttp3)
    {
        HttpServerTlsOptions? tls = _options.Tls;
        if (tls is null && _options.SharedTls is not null)
        {
            var certificate = _options.SharedTls.GetCertificate();
            tls = new HttpServerTlsOptions
            {
                Certificate = certificate,
                ClientCertificateMode = _options.ClientCertificateMode,
                CheckCertificateRevocation = _options.CheckCertificateRevocation
            };
        }

        if (tls?.Certificate is null)
        {
            return null;
        }

        return new HttpsConnectionAdapterOptions
        {
            ServerCertificate = tls.Certificate,
            ClientCertificateMode = tls.ClientCertificateMode,
            CheckCertificateRevocation = tls.CheckCertificateRevocation ?? !enableHttp3
        };
    }

    private static void ApplyRuntimeLimits(KestrelServerOptions options, HttpServerRuntimeOptions? runtime)
    {
        if (runtime is null)
        {
            return;
        }

        if (runtime.MaxRequestBodySize is { } maxBody)
        {
            options.Limits.MaxRequestBodySize = maxBody;
        }

        if (runtime.MaxRequestHeadersTotalSize is { } maxHeaders)
        {
            options.Limits.MaxRequestHeadersTotalSize = maxHeaders;
        }

        if (runtime.KeepAliveTimeout is { } keepAlive)
        {
            options.Limits.KeepAliveTimeout = keepAlive;
        }

        if (runtime.RequestHeadersTimeout is { } headersTimeout)
        {
            options.Limits.RequestHeadersTimeout = headersTimeout;
        }
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

        serverOptions.ListenAnyIP(endpoint.Port, configure);
    }
}
