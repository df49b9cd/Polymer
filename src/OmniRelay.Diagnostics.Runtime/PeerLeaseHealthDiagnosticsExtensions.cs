using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OmniRelay.Diagnostics.Alerting;

namespace OmniRelay.Diagnostics;

/// <summary>ServiceCollection helpers for peer lease health diagnostics.</summary>
public static class PeerLeaseHealthDiagnosticsExtensions
{
    public static IServiceCollection AddPeerLeaseHealthDiagnostics(
        this IServiceCollection services,
        Action<PeerLeaseHealthOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPeerHealthSnapshotProvider, PeerLeaseHealthTracker>());
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetService<IOptions<PeerLeaseHealthOptions>>()?.Value ?? new PeerLeaseHealthOptions();
            var tracker = new PeerLeaseHealthTracker(
                options.HeartbeatGracePeriod,
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetService<IAlertPublisher>());
            return tracker;
        });
        return services;
    }
}

/// <summary>Options for configuring the peer lease health tracker.</summary>
public sealed class PeerLeaseHealthOptions
{
    public TimeSpan? HeartbeatGracePeriod { get; set; }
}
