using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OmniRelay.Dispatcher;
using OmniRelay.Dispatcher.Config;
using OmniRelay.FeatureTests.Fixtures;
using Xunit;

namespace OmniRelay.FeatureTests.Features.DispatcherBootstrap;

[Collection(nameof(FeatureTestCollection))]
public sealed class DispatcherBootstrapFeatureTests(FeatureTestApplication application)
{
    private readonly FeatureTestApplication _application = application;

    [Fact(DisplayName = "Dispatcher host boots with feature configuration", Timeout = TestTimeouts.Default)]
    public void DispatcherStartsWithFeatureConfiguration()
    {
        var dispatcher = _application.Services.GetRequiredService<Dispatcher.Dispatcher>();

        Assert.Equal("feature-tests-relay", dispatcher.ServiceName);
        Assert.Equal(DispatcherStatus.Running, dispatcher.Status);
    }

    [Fact(DisplayName = "Feature configuration binds logging and diagnostics overrides", Timeout = TestTimeouts.Default)]
    public void ConfigurationBindingsAreApplied()
    {
        var options = _application.Services.GetRequiredService<IOptionsMonitor<OmniRelayConfigurationOptions>>();
        var snapshot = options.CurrentValue;

        Assert.Equal("feature-tests-relay", snapshot.Service);
        Assert.Equal("Debug", snapshot.Logging.Level);
        Assert.Equal("Warning", snapshot.Logging.Overrides["System.Net.Http"]);
        Assert.False(snapshot.Diagnostics.OpenTelemetry.Enabled ?? true);
    }
}
