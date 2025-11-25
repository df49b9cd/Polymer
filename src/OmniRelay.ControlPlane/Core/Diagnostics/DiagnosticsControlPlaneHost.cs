using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniRelay.ControlPlane.Hosting;
using OmniRelay.Identity;
using OmniRelay.ControlPlane.Upgrade;
using OmniRelay.Core.Gossip;
using OmniRelay.Core.Leadership;
using OmniRelay.Core.Shards.ControlPlane;
using OmniRelay.Core.Transport;
using OmniRelay.Diagnostics;

namespace OmniRelay.Core.Diagnostics;

/// <summary>Dedicated HTTP host that surfaces diagnostics + leadership control-plane endpoints.</summary>
internal sealed class DiagnosticsControlPlaneHost : ILifecycle, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly HttpControlPlaneHostOptions _options;
    private readonly DiagnosticsControlPlaneFeatures _features;
    private readonly ILogger<DiagnosticsControlPlaneHost> _logger;
    private readonly TransportTlsManager? _tlsManager;
    private WebApplication? _app;
    private Task? _hostTask;
    private CancellationTokenSource? _cts;

    public DiagnosticsControlPlaneHost(
        IServiceProvider services,
        HttpControlPlaneHostOptions options,
        bool enableLoggingToggle,
        bool enableSamplingToggle,
        bool enableLeaseHealthDiagnostics,
        bool enablePeerDiagnostics,
        bool enableLeadershipDiagnostics,
        bool enableDocumentation,
        bool enableProbeDiagnostics,
        bool enableChaosControl,
        bool enableShardDiagnostics,
        ILogger<DiagnosticsControlPlaneHost> logger,
        TransportTlsManager? tlsManager = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _features = new DiagnosticsControlPlaneFeatures(enableLoggingToggle, enableSamplingToggle, enableLeaseHealthDiagnostics, enablePeerDiagnostics, enableLeadershipDiagnostics, enableDocumentation, enableProbeDiagnostics, enableChaosControl, enableShardDiagnostics);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tlsManager = tlsManager;
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var builder = new HttpControlPlaneHostBuilder(_options);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(_ => _services.GetRequiredService<IDiagnosticsRuntime>());
            services.AddSingleton(_ => _services.GetRequiredService<IMeshGossipAgent>());
            services.AddSingleton<IEnumerable<IPeerHealthSnapshotProvider>>(_ => _services.GetServices<IPeerHealthSnapshotProvider>());
            var peerDiagnosticsProvider = _services.GetService<IPeerDiagnosticsProvider>();
            if (peerDiagnosticsProvider is not null)
            {
                services.AddSingleton(peerDiagnosticsProvider);
            }
            else
            {
                services.AddSingleton<IPeerDiagnosticsProvider, NullPeerDiagnosticsProvider>();
            }

            if (_features.EnableProbeDiagnostics)
            {
                services.AddSingleton(_ => _services.GetRequiredService<IProbeSnapshotProvider>());
            }

            if (_features.EnableChaosControl)
            {
                services.AddSingleton(_ => _services.GetRequiredService<ChaosCoordinator>());
            }

            var leadershipObserver = _services.GetService<ILeadershipObserver>();
            if (leadershipObserver is not null)
            {
                services.AddSingleton(leadershipObserver);
            }

            var drainCoordinator = _services.GetService<NodeDrainCoordinator>();
            if (drainCoordinator is not null)
            {
                services.AddSingleton(drainCoordinator);
            }

            var shardService = _services.GetService<ShardControlPlaneService>();
            if (shardService is not null)
            {
                services.AddSingleton(shardService);
            }
        });

        builder.ConfigureApp(ConfigureAppCore);

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
                    // Expected when the linked CTS is cancelled during shutdown.
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

    private void ConfigureAppCore(WebApplication app)
    {
        app.UseOmniRelayDiagnosticsControlPlane(options =>
        {
            options.EnableLoggingToggle = _features.EnableLoggingToggle;
            options.EnableTraceSamplingToggle = _features.EnableSamplingToggle;
            options.EnableLeaseHealthDiagnostics = _features.EnableLeaseHealthDiagnostics;
            options.EnablePeerDiagnostics = _features.EnablePeerDiagnostics;
        });

        if (_features.EnableDocumentation)
        {
            app.MapOmniRelayDocumentation();
        }

        if (_features.EnableProbeDiagnostics || _features.EnableChaosControl)
        {
            app.MapOmniRelayProbeDiagnostics(options =>
            {
                options.EnableProbeResults = _features.EnableProbeDiagnostics;
                options.EnableChaosControl = _features.EnableChaosControl;
            });
        }

        if (_features.EnableLeadershipDiagnostics)
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path == "/control/leaders" && HttpMethods.IsGet(context.Request.Method))
                {
                    await GetLeadershipSnapshot(context).ConfigureAwait(false);
                    return;
                }

                if (context.Request.Path == "/control/events/leadership" && HttpMethods.IsGet(context.Request.Method))
                {
                    await StreamLeadershipEvents(context).ConfigureAwait(false);
                    return;
                }

                await next().ConfigureAwait(false);
            });
        }

        if (_features.EnableShardDiagnostics)
        {
            app.MapShardDiagnosticsEndpoints();
        }

        MapUpgradeEndpoints(app);
    }

    private static async Task GetLeadershipSnapshot(HttpContext context)
    {
        var observer = context.RequestServices.GetService<ILeadershipObserver>();
        var snapshot = observer?.Snapshot() ?? new LeadershipSnapshot(DateTimeOffset.UtcNow, []);
        if (context.Request.Query.TryGetValue("scope", out var scopeValues) && !string.IsNullOrWhiteSpace(scopeValues))
        {
            var scopeFilter = scopeValues.ToString();
            var filtered = snapshot.Tokens
                .Where(token => string.Equals(token.Scope, scopeFilter, StringComparison.OrdinalIgnoreCase))
                .ToImmutableArray();
            snapshot = new LeadershipSnapshot(snapshot.GeneratedAt, filtered);
        }

        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                snapshot,
                DiagnosticsControlPlaneJsonContext.Default.LeadershipSnapshot,
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static Task StreamLeadershipEvents(HttpContext context) =>
        StreamLeadershipEventsAsync(
            context,
            context.RequestServices.GetRequiredService<ILeadershipObserver>(),
            context.RequestServices.GetRequiredService<ILogger<LeadershipEventStreamMarker>>());

    private static async Task StreamLeadershipEventsAsync(HttpContext context, ILeadershipObserver observer, ILogger<LeadershipEventStreamMarker> logger)
    {
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["Content-Type"] = "text/event-stream";
        var scopeFilter = context.Request.Query.TryGetValue("scope", out var scopeValues)
            ? scopeValues.ToString()
            : null;
        var scopeLabel = string.IsNullOrWhiteSpace(scopeFilter) ? "*" : scopeFilter!;
        var transport = context.Request.Protocol;
        LeadershipDiagnosticsLog.LeadershipStreamOpened(logger, scopeLabel, transport);

        try
        {
            await foreach (var leadershipEvent in observer.SubscribeAsync(scopeFilter, context.RequestAborted).ConfigureAwait(false))
            {
                var payload = JsonSerializer.Serialize(
                    leadershipEvent,
                    DiagnosticsControlPlaneJsonContext.Default.LeadershipEvent);
                await context.Response.WriteAsync($"data: {payload}\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            LeadershipDiagnosticsLog.LeadershipStreamClosed(logger, scopeLabel);
        }
    }

    private static void MapUpgradeEndpoints(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value;
            var method = context.Request.Method;
            if (path is "/control/upgrade" && HttpMethods.IsGet(method))
            {
                await GetUpgradeSnapshot(context).ConfigureAwait(false);
                return;
            }

            if (path is "/control/upgrade/drain" && HttpMethods.IsPost(method))
            {
                await BeginDrainAsync(context).ConfigureAwait(false);
                return;
            }

            if (path is "/control/upgrade/resume" && HttpMethods.IsPost(method))
            {
                await ResumeDrainAsync(context).ConfigureAwait(false);
                return;
            }

            await next().ConfigureAwait(false);
        });
    }

    private static async Task GetUpgradeSnapshot(HttpContext context)
    {
        var coordinator = context.RequestServices.GetRequiredService<NodeDrainCoordinator>();
        var snapshot = coordinator.Snapshot();
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                snapshot,
                ShardDiagnosticsJsonContext.Default.NodeDrainSnapshot,
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static async Task BeginDrainAsync(HttpContext context)
    {
        var coordinator = context.RequestServices.GetRequiredService<NodeDrainCoordinator>();
        var request = await context.Request.ReadFromJsonAsync(
            ShardDiagnosticsJsonContext.Default.NodeDrainCommand,
            context.RequestAborted).ConfigureAwait(false);
        try
        {
            var snapshot = await coordinator.BeginDrainAsync(request?.Reason, context.RequestAborted).ConfigureAwait(false);
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                    context.Response.Body,
                    snapshot,
                    ShardDiagnosticsJsonContext.Default.NodeDrainSnapshot,
                    context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict)
                .ExecuteAsync(context)
                .ConfigureAwait(false);
        }
    }

    private static async Task ResumeDrainAsync(HttpContext context)
    {
        var coordinator = context.RequestServices.GetRequiredService<NodeDrainCoordinator>();
        try
        {
            var snapshot = await coordinator.ResumeAsync(context.RequestAborted).ConfigureAwait(false);
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                    context.Response.Body,
                    snapshot,
                    ShardDiagnosticsJsonContext.Default.NodeDrainSnapshot,
                    context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict)
                .ExecuteAsync(context)
                .ConfigureAwait(false);
        }
    }

}

public sealed record NodeDrainCommand(string? Reason);

internal readonly record struct DiagnosticsControlPlaneFeatures(
    bool EnableLoggingToggle,
    bool EnableSamplingToggle,
    bool EnableLeaseHealthDiagnostics,
    bool EnablePeerDiagnostics,
    bool EnableLeadershipDiagnostics,
    bool EnableDocumentation,
    bool EnableProbeDiagnostics,
    bool EnableChaosControl,
    bool EnableShardDiagnostics);
