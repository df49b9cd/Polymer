using System.Diagnostics.Metrics;
using Hugo;
using Microsoft.Extensions.Hosting;

namespace OmniRelay.Diagnostics;

/// <summary>
/// Hosted service that configures Hugo/Go diagnostics with the application's <see cref="IMeterFactory"/>.
/// </summary>
public sealed class DiagnosticsRegistrationHostedService(IMeterFactory? meterFactory = null) : IHostedService
{
    /// <summary>Registers the metrics factory on start.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (meterFactory is not null)
        {
            GoDiagnostics.Configure(meterFactory);
        }

        return Task.CompletedTask;
    }

    /// <summary>No-op on stop.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
