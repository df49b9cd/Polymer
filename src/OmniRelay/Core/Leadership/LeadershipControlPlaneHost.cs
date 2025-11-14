using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniRelay.ControlPlane.Hosting;
using OmniRelay.ControlPlane.Security;
using OmniRelay.Core.Transport;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Grpc.Interceptors;

namespace OmniRelay.Core.Leadership;

/// <summary>Dedicated gRPC host for leadership control-plane streaming APIs.</summary>
internal sealed class LeadershipControlPlaneHost : ILifecycle, IDisposable, IGrpcServerInterceptorSink
{
    private readonly IServiceProvider _services;
    private readonly GrpcControlPlaneHostOptions _options;
    private readonly ILogger<LeadershipControlPlaneHost> _logger;
    private readonly TransportTlsManager? _tlsManager;
    private WebApplication? _app;
    private Task? _hostTask;
    private CancellationTokenSource? _cts;
    private GrpcServerInterceptorRegistry? _serverInterceptors;

    public LeadershipControlPlaneHost(
        IServiceProvider services,
        GrpcControlPlaneHostOptions options,
        ILogger<LeadershipControlPlaneHost> logger,
        TransportTlsManager? tlsManager = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tlsManager = tlsManager;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        if (_options.Urls.Count == 0)
        {
            _logger.LogInformation("Leadership control-plane gRPC host skipped because no URLs were configured.");
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var builder = new GrpcControlPlaneHostBuilder(_options);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(_ => _services.GetRequiredService<LeadershipControlGrpcService>());
        });
        builder.ConfigureApp(app => app.MapGrpcService<LeadershipControlGrpcService>());

        ConfigureInterceptorPipeline(builder);

        var app = builder.Build();
        await app.StartAsync(_cts.Token).ConfigureAwait(false);
        _app = app;
        _hostTask = app.WaitForShutdownAsync(_cts.Token);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null)
        {
            return;
        }

        using var cts = _cts;
        _cts = null;
        var hostTask = _hostTask;
        _hostTask = null;

        try
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
            if (hostTask is not null)
            {
                try
                {
                    await hostTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            await _app.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _app = null;
        }
    }

    public void Dispose()
    {
        _tlsManager?.Dispose();
    }

    void IGrpcServerInterceptorSink.AttachGrpcServerInterceptors(GrpcServerInterceptorRegistry registry)
    {
        _serverInterceptors = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    private void ConfigureInterceptorPipeline(GrpcControlPlaneHostBuilder builder)
    {
        var registry = _serverInterceptors;
        if (registry is null)
        {
            return;
        }

        builder.ConfigureServices(services =>
        {
            services.AddSingleton(new CompositeServerInterceptor(registry));
            services.AddGrpc(options =>
            {
                options.Interceptors.Add<CompositeServerInterceptor>();
            });
        });
    }
}


