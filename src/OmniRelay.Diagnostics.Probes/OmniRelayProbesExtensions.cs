using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace OmniRelay.Diagnostics;

/// <summary>DI helpers for registering probes and chaos experiments.</summary>
public static class OmniRelayProbesExtensions
{
    public static IServiceCollection AddOmniRelayProbes(
        this IServiceCollection services,
        bool enableScheduler = true,
        Action<ProbeRegistrationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new ProbeRegistrationBuilder(services);
        configure?.Invoke(builder);

        services.TryAddSingleton<ProbeSnapshotStore>();
        services.TryAddSingleton<IProbeSnapshotProvider>(sp => sp.GetRequiredService<ProbeSnapshotStore>());
        if (enableScheduler)
        {
            services.AddHostedService<ProbeSchedulerHostedService>();
        }

        services.TryAddSingleton<ChaosCoordinator>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChaosExperiment, NullChaosExperiment>());

        services.AddOptions<ProbeDiagnosticsOptions>();
        return services;
    }

    public static IEndpointRouteBuilder MapOmniRelayProbeDiagnostics(
        this IEndpointRouteBuilder builder,
        Action<ProbeDiagnosticsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = builder.ServiceProvider.GetRequiredService<IOptions<ProbeDiagnosticsOptions>>().Value;
        configure?.Invoke(options);

        if (options.EnableProbeResults)
        {
            var probeEndpoint = builder.MapGet("/omnirelay/control/probes", (IProbeSnapshotProvider provider) => Results.Json(provider.Snapshot()));
            if (!string.IsNullOrWhiteSpace(options.ProbeAuthorizationPolicy))
            {
                probeEndpoint.RequireAuthorization(options.ProbeAuthorizationPolicy);
            }
        }

        if (options.EnableChaosControl)
        {
            var startEndpoint = builder.MapPost("/omnirelay/control/chaos/{name}:start", async (string name, ChaosCoordinator coordinator, CancellationToken cancellationToken) =>
            {
                var result = await coordinator.StartAsync(name, cancellationToken).ConfigureAwait(false);
                return result.ToHttpResult();
            });

            var stopEndpoint = builder.MapPost("/omnirelay/control/chaos/{name}:stop", async (string name, ChaosCoordinator coordinator, CancellationToken cancellationToken) =>
            {
                var result = await coordinator.StopAsync(name, cancellationToken).ConfigureAwait(false);
                return result.ToHttpResult();
            });

            if (!string.IsNullOrWhiteSpace(options.ChaosAuthorizationPolicy))
            {
                startEndpoint.RequireAuthorization(options.ChaosAuthorizationPolicy);
                stopEndpoint.RequireAuthorization(options.ChaosAuthorizationPolicy);
            }
        }

        return builder;
    }
}

/// <summary>Options for probe + chaos diagnostic endpoints.</summary>
public sealed class ProbeDiagnosticsOptions
{
    public bool EnableProbeResults { get; set; } = true;

    public bool EnableChaosControl { get; set; } = false;

    public string? ProbeAuthorizationPolicy { get; set; }
        = null;

    public string? ChaosAuthorizationPolicy { get; set; }
        = null;
}

/// <summary>Builder to register probes.</summary>
public sealed class ProbeRegistrationBuilder
{
    private readonly IServiceCollection _services;

    internal ProbeRegistrationBuilder(IServiceCollection services) => _services = services;

    public ProbeRegistrationBuilder AddProbe<TProbe>() where TProbe : class, IHealthProbe
    {
        _services.AddSingleton<IHealthProbe, TProbe>();
        return this;
    }

    public ProbeRegistrationBuilder AddChaosExperiment<TExperiment>() where TExperiment : class, IChaosExperiment
    {
        _services.AddSingleton<IChaosExperiment, TExperiment>();
        return this;
    }
}
