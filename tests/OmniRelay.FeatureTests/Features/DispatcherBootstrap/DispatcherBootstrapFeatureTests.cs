using AwesomeAssertions;
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

        dispatcher.ServiceName.Should().Be("feature-tests-relay");
        dispatcher.Status.Should().Be(DispatcherStatus.Running);
    }

    [Fact(DisplayName = "Feature configuration binds logging and diagnostics overrides", Timeout = TestTimeouts.Default)]
    public void ConfigurationBindingsAreApplied()
    {
        var options = _application.Services.GetRequiredService<IOptionsMonitor<OmniRelayConfigurationOptions>>();
        var snapshot = options.CurrentValue;

        snapshot.Service.Should().Be("feature-tests-relay");
        snapshot.Logging.Level.Should().Be("Debug");
        snapshot.Logging.Overrides["System.Net.Http"].Should().Be("Warning");
        (snapshot.Diagnostics.OpenTelemetry.Enabled ?? true).Should().BeFalse();
    }
}
