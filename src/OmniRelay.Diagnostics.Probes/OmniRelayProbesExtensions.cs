using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace OmniRelay.Diagnostics;

/// <summary>DI helpers for registering probes and chaos experiments without Minimal APIs (AOT-safe).</summary>
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

    public static void MapOmniRelayProbeDiagnostics(
        this WebApplication app,
        Action<ProbeDiagnosticsOptions>? configure = null)
    {
        var options = app.Services.GetRequiredService<IOptions<ProbeDiagnosticsOptions>>().Value;
        configure?.Invoke(options);

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value;
            var method = context.Request.Method;

            if (path is "/omnirelay/control/probes" && HttpMethods.IsGet(method) && options.EnableProbeResults)
            {
                await WriteProbeResultsAsync(context).ConfigureAwait(false);
                return;
            }

            if (path is not null && path.StartsWith("/omnirelay/control/chaos/", StringComparison.OrdinalIgnoreCase) && options.EnableChaosControl)
            {
                var name = path.Substring("/omnirelay/control/chaos/".Length);
                var coordinator = context.RequestServices.GetRequiredService<ChaosCoordinator>();

                if (name.EndsWith(":start", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(method))
                {
                    var target = name[..^":start".Length];
                    var result = await coordinator.StartAsync(target, context.RequestAborted).ConfigureAwait(false);
                    await result.ToHttpResult().ExecuteAsync(context).ConfigureAwait(false);
                    return;
                }

                if (name.EndsWith(":stop", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(method))
                {
                    var target = name[..^":stop".Length];
                    var result = await coordinator.StopAsync(target, context.RequestAborted).ConfigureAwait(false);
                    await result.ToHttpResult().ExecuteAsync(context).ConfigureAwait(false);
                    return;
                }
            }

            await next().ConfigureAwait(false);
        });
    }

    private static async Task WriteProbeResultsAsync(HttpContext context)
    {
        var provider = context.RequestServices.GetRequiredService<IProbeSnapshotProvider>();
        var snapshot = provider.Snapshot();
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                snapshot,
                ProbeDiagnosticsJsonContext.Default.IReadOnlyCollectionProbeExecutionSnapshot,
                context.RequestAborted)
            .ConfigureAwait(false);
    }
}

/// <summary>Options for probe + chaos diagnostic endpoints.</summary>
public sealed class ProbeDiagnosticsOptions
{
    public bool EnableProbeResults { get; set; } = true;

    public bool EnableChaosControl { get; set; }

    public string? ProbeAuthorizationPolicy { get; set; }

    public string? ChaosAuthorizationPolicy { get; set; }
}

/// <summary>Builder to register probes.</summary>
public sealed class ProbeRegistrationBuilder
{
    private readonly IServiceCollection _services;

    internal ProbeRegistrationBuilder(IServiceCollection services) => _services = services;

    public ProbeRegistrationBuilder AddProbe<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProbe>()
        where TProbe : class, IHealthProbe
    {
        _services.AddSingleton<IHealthProbe, TProbe>();
        return this;
    }

    public ProbeRegistrationBuilder AddChaosExperiment<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TExperiment>()
        where TExperiment : class, IChaosExperiment
    {
        _services.AddSingleton<IChaosExperiment, TExperiment>();
        return this;
    }
}
