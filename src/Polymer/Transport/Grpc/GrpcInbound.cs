using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    public GrpcInbound(
        IEnumerable<string> urls,
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null)
    {
        _urls = urls?.ToArray() ?? throw new ArgumentNullException(nameof(urls));
        if (_urls.Length == 0)
        {
            throw new ArgumentException("At least one URL must be provided for the gRPC inbound.", nameof(urls));
        }

        _configureServices = configureServices;
        _configureApp = configureApp;
    }

    public IReadOnlyCollection<string> Urls => _app?.Urls ?? Array.Empty<string>();

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
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            foreach (var url in _urls)
            {
                var uri = new Uri(url);
                var host = string.Equals(uri.Host, "*", StringComparison.Ordinal) ? System.Net.IPAddress.Any : System.Net.IPAddress.Parse(uri.Host);
                serverOptions.Listen(host, uri.Port, listenOptions =>
                {
                    listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
                });
            }
        });

        builder.Services.AddGrpc();
        builder.Services.AddSingleton(_dispatcher);
        builder.Services.AddSingleton<DispatcherGrpcService>();
        _configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        _configureApp?.Invoke(app);

        app.MapGrpcService<DispatcherGrpcService>();

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
