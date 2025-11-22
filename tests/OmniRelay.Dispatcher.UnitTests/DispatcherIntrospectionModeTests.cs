using AwesomeAssertions;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public class DispatcherIntrospectionModeTests
{
    [Fact]
    public void Introspect_Should_Include_Mode_And_Capabilities()
    {
        var options = new DispatcherOptions("svc") { Mode = DeploymentMode.Sidecar };
        var dispatcher = new global::OmniRelay.Dispatcher.Dispatcher(options);

        var snapshot = dispatcher.Introspect();

        snapshot.Mode.Should().Be(DeploymentMode.Sidecar);
        snapshot.Capabilities.Should().Contain("deployment:sidecar");
        snapshot.Capabilities.Should().Contain("feature:http");
        snapshot.Capabilities.Should().Contain("feature:grpc");
    }
}
