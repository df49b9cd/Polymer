using System.Diagnostics.Metrics;
using Hugo;
using Microsoft.Extensions.Hosting;

namespace OmniRelay.Configuration.Internal;

internal sealed class DiagnosticsRegistrationHostedService(IMeterFactory? meterFactory = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (meterFactory is not null)
        {
            GoDiagnostics.Configure(meterFactory);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
