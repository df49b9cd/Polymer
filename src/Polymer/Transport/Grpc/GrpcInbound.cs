using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Grpc.AspNetCore.Server.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Polymer.Core.Transport;
using Polymer.Dispatcher;

namespace Polymer.Transport.Grpc;

public sealed class GrpcInbound : ILifecycle, IDispatcherAware
{
    private readonly string[] _urls;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly Action<WebApplication>? _configureApp;
    private WebApplication? _app;
    private Dispatcher.Dispatcher? _dispatcher;
    private readonly GrpcServerTlsOptions? _serverTlsOptions;
    private readonly GrpcServerRuntimeOptions? _serverRuntimeOptions;

    public GrpcInbound(
        IEnumerable<string> urls,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null,
        GrpcServerTlsOptions? serverTlsOptions = null,
        GrpcServerRuntimeOptions? serverRuntimeOptions = null)
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
            throw new InvalidOperationException("Dispatcher must be bound before starting the gRPC inbound.");
        }

        var builder = WebApplication.CreateSlimBuilder();

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
                    listenOptions.Protocols = HttpProtocols.Http2;
                    if (_serverTlsOptions is { } tlsOptions)
                    {
                        var httpsOptions = new HttpsConnectionAdapterOptions
                        {
                            ServerCertificate = tlsOptions.Certificate,
                            ClientCertificateMode = tlsOptions.ClientCertificateMode
                        };

                        if (tlsOptions.EnabledProtocols is { })
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
        }).UseUrls(_urls);

        builder.Services.AddSingleton(_dispatcher);
        builder.Services.AddSingleton<IServiceMethodProvider<GrpcDispatcherService>>(
            _ => new GrpcDispatcherServiceMethodProvider(_dispatcher));
        builder.Services.AddSingleton<GrpcDispatcherService>();
        builder.Services.AddGrpc(options =>
        {
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
            }
        });

        _configureServices?.Invoke(builder.Services);

        var app = builder.Build();

        _configureApp?.Invoke(app);

        app.MapGrpcService<GrpcDispatcherService>();

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
}
