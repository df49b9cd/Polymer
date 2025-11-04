using System.Diagnostics.Metrics;
using Hugo;
using Microsoft.Extensions.Hosting;

namespace YARPCore.Configuration.Internal;

internal sealed class DiagnosticsRegistrationHostedService(IMeterFactory? meterFactory = null) : IHostedService
{
    private readonly IMeterFactory? _meterFactory = meterFactory;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_meterFactory is not null)
        {
            GoDiagnostics.Configure(_meterFactory);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
