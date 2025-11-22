using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OmniRelay.Diagnostics;

/// <summary>ServiceCollection helpers for diagnostics runtime state and endpoints.</summary>
public static class DiagnosticsRuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddOmniRelayDiagnosticsRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<DiagnosticsRuntimeState>();
        services.TryAddSingleton<IDiagnosticsRuntime>(sp => sp.GetRequiredService<DiagnosticsRuntimeState>());
        return services;
    }
}
