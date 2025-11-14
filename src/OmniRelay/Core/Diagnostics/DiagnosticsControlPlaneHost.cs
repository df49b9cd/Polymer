using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniRelay.ControlPlane.Hosting;
using OmniRelay.ControlPlane.Security;
using OmniRelay.ControlPlane.Upgrade;
using OmniRelay.Core.Gossip;
using OmniRelay.Core.Leadership;
using OmniRelay.Core.Peers;
using OmniRelay.Core.Transport;

namespace OmniRelay.Core.Diagnostics;

/// <summary>Dedicated HTTP host that surfaces diagnostics + leadership control-plane endpoints.</summary>
internal sealed class DiagnosticsControlPlaneHost : ILifecycle, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly HttpControlPlaneHostOptions _options;
    private readonly DiagnosticsControlPlaneFeatures _features;
    private readonly ILogger<DiagnosticsControlPlaneHost> _logger;
    private readonly TransportTlsManager? _tlsManager;
    private static readonly JsonSerializerOptions LeadershipEventJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<LeadershipEventKind>(JsonNamingPolicy.CamelCase) }
    };
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
        ILogger<DiagnosticsControlPlaneHost> logger,
        TransportTlsManager? tlsManager = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _features = new DiagnosticsControlPlaneFeatures(enableLoggingToggle, enableSamplingToggle, enableLeaseHealthDiagnostics, enablePeerDiagnostics, enableLeadershipDiagnostics);
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
        });

        builder.ConfigureApp(ConfigureApp);

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

    [RequiresDynamicCode("Diagnostics control plane endpoints use minimal APIs with reflection.")]
    [RequiresUnreferencedCode("Diagnostics control plane endpoints use minimal APIs with reflection.")]
    private void ConfigureApp(WebApplication app)
    {
        if (_features.EnableLoggingToggle)
        {
            app.MapGet("/omnirelay/control/logging", (IDiagnosticsRuntime runtime) =>
            {
                var level = runtime.MinimumLogLevel?.ToString();
                return Results.Json(new { minimumLevel = level });
            });

            app.MapPost("/omnirelay/control/logging", (DiagnosticsLogLevelRequest request, IDiagnosticsRuntime runtime) =>
            {
                if (request is null)
                {
                    return Results.BadRequest(new { error = "Request body required." });
                }

                if (string.IsNullOrWhiteSpace(request.Level))
                {
                    runtime.SetMinimumLogLevel(null);
                    return Results.NoContent();
                }

                if (!Enum.TryParse<LogLevel>(request.Level, ignoreCase: true, out var parsed))
                {
                    return Results.BadRequest(new { error = $"Invalid log level '{request.Level}'." });
                }

                runtime.SetMinimumLogLevel(parsed);
                return Results.NoContent();
            });
        }

        if (_features.EnableSamplingToggle)
        {
            app.MapGet("/omnirelay/control/tracing", (IDiagnosticsRuntime runtime) =>
            {
                return Results.Json(new { samplingProbability = runtime.TraceSamplingProbability });
            });

            app.MapPost("/omnirelay/control/tracing", (DiagnosticsSamplingRequest request, IDiagnosticsRuntime runtime) =>
            {
                if (request is null)
                {
                    return Results.BadRequest(new { error = "Request body required." });
                }

                try
                {
                    runtime.SetTraceSamplingProbability(request.Probability);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }

                return Results.NoContent();
            });
        }

        if (_features.EnableLeaseHealthDiagnostics)
        {
            app.MapGet("/omnirelay/control/lease-health", (IEnumerable<IPeerHealthSnapshotProvider> providers) =>
            {
                var builder = ImmutableArray.CreateBuilder<PeerLeaseHealthSnapshot>();
                foreach (var provider in providers)
                {
                    if (provider is null)
                    {
                        continue;
                    }

                    var snapshot = provider.Snapshot();
                    if (!snapshot.IsDefaultOrEmpty)
                    {
                        builder.AddRange(snapshot);
                    }
                }

                var diagnostics = PeerLeaseHealthDiagnostics.FromSnapshots(builder.ToImmutable());
                return Results.Json(diagnostics);
            });
        }

        if (_features.EnablePeerDiagnostics)
        {
            static IResult HandlePeers(IMeshGossipAgent agent)
            {
                var snapshot = agent.Snapshot();
                var peers = snapshot.Members.Select(member => new
                {
                    member.NodeId,
                    status = member.Status.ToString(),
                    member.LastSeen,
                    rttMs = member.RoundTripTimeMs,
                    metadata = new
                    {
                        member.Metadata.Role,
                        member.Metadata.ClusterId,
                        member.Metadata.Region,
                        member.Metadata.MeshVersion,
                        member.Metadata.Http3Support,
                        member.Metadata.Endpoint,
                        member.Metadata.MetadataVersion,
                        Labels = member.Metadata.Labels
                    }
                });

                return Results.Json(new
                {
                    snapshot.SchemaVersion,
                    snapshot.GeneratedAt,
                    snapshot.LocalNodeId,
                    peers
                });
            }

            app.MapGet("/control/peers", HandlePeers);
            app.MapGet("/omnirelay/control/peers", HandlePeers);
        }

        if (_features.EnableLeadershipDiagnostics)
        {
            app.MapGet("/control/leaders", (HttpRequest request, ILeadershipObserver observer) =>
            {
                var snapshot = observer?.Snapshot() ?? new LeadershipSnapshot(DateTimeOffset.UtcNow, []);
                if (request.Query.TryGetValue("scope", out var scopeValues) && !string.IsNullOrWhiteSpace(scopeValues))
                {
                    var scopeFilter = scopeValues.ToString();
                    var filtered = snapshot.Tokens
                        .Where(token => string.Equals(token.Scope, scopeFilter, StringComparison.OrdinalIgnoreCase))
                        .ToImmutableArray();
                    snapshot = new LeadershipSnapshot(snapshot.GeneratedAt, filtered);
                }

                return Results.Json(snapshot);
            });

            app.MapGet("/control/events/leadership", async (HttpContext context, ILeadershipObserver observer, ILogger<LeadershipEventStreamMarker> logger) =>
            {
                await StreamLeadershipEventsAsync(context, observer, logger).ConfigureAwait(false);
            });
        }

        MapUpgradeEndpoints(app);
    }

    [RequiresDynamicCode("Diagnostics control plane streaming uses System.Text.Json reflection serialization.")]
    [RequiresUnreferencedCode("Diagnostics control plane streaming uses System.Text.Json reflection serialization.")]
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
                var payload = JsonSerializer.Serialize(leadershipEvent, LeadershipEventJsonOptions);
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
        app.MapGet("/control/upgrade", (NodeDrainCoordinator coordinator) =>
        {
            var snapshot = coordinator.Snapshot();
            return Results.Json(snapshot);
        });

        app.MapPost("/control/upgrade/drain", async (HttpContext context, NodeDrainCoordinator coordinator, NodeDrainCommand? request) =>
        {
            try
            {
                var snapshot = await coordinator.BeginDrainAsync(request?.Reason, context.RequestAborted).ConfigureAwait(false);
                return Results.Json(snapshot);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapPost("/control/upgrade/resume", async (HttpContext context, NodeDrainCoordinator coordinator) =>
        {
            try
            {
                var snapshot = await coordinator.ResumeAsync(context.RequestAborted).ConfigureAwait(false);
                return Results.Json(snapshot);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });
    }

}

internal sealed record NodeDrainCommand(string? Reason);

internal readonly record struct DiagnosticsControlPlaneFeatures(
    bool EnableLoggingToggle,
    bool EnableSamplingToggle,
    bool EnableLeaseHealthDiagnostics,
    bool EnablePeerDiagnostics,
    bool EnableLeadershipDiagnostics);

