using System.Diagnostics.Metrics;
using OmniRelay.Configuration.Internal;
using Xunit;

namespace OmniRelay.Configuration.UnitTests.Configuration;

public sealed class DiagnosticsRegistrationHostedServiceTests
{
    [Fact]
    public async Task StartAsync_WithMeterFactory_CompletesSuccessfully()
    {
        var factory = new RecordingMeterFactory();
        var hostedService = new DiagnosticsRegistrationHostedService(factory);

        await hostedService.StartAsync(CancellationToken.None);
        await hostedService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_WithoutMeterFactory_IsNoOp()
    {
        var hostedService = new DiagnosticsRegistrationHostedService();

        await hostedService.StartAsync(CancellationToken.None);
        await hostedService.StopAsync(CancellationToken.None);
    }

    private sealed class RecordingMeterFactory : IMeterFactory
    {
        public List<Meter> CreatedMeters { get; } = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options.Name, options.Version);
            CreatedMeters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in CreatedMeters)
            {
                meter.Dispose();
            }
        }
    }
}
