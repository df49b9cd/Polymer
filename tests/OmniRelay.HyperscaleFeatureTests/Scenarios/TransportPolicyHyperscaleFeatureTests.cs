using OmniRelay.Dispatcher.Config;
using OmniRelay.Tests.Support;
using Xunit;

namespace OmniRelay.HyperscaleFeatureTests.Scenarios;

public sealed class TransportPolicyHyperscaleFeatureTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void TransportPolicyEvaluator_HandlesThousandsOfConfigurations()
    {
        var options = Enumerable.Range(0, 2_000)
            .Select(index => CreateOptions(index % 2 == 0))
            .ToArray();

        Parallel.ForEach(options, option => TransportPolicyEvaluator.Enforce(option));
    }

    private static OmniRelayConfigurationOptions CreateOptions(bool enableHttp3)
    {
        var options = new OmniRelayConfigurationOptions
        {
            Service = $"mesh-hyperscale-{Guid.NewGuid():N}"
        };

        options.Diagnostics.ControlPlane.HttpUrls.Clear();
        options.Diagnostics.ControlPlane.HttpUrls.Add("https://127.0.0.1:9443");
        options.Diagnostics.ControlPlane.HttpRuntime.EnableHttp3 = enableHttp3;
        options.Diagnostics.ControlPlane.GrpcUrls.Clear();
        options.Diagnostics.ControlPlane.GrpcUrls.Add("https://127.0.0.1:9444");
        options.Diagnostics.ControlPlane.GrpcRuntime.EnableHttp3 = true;

        if (!enableHttp3)
        {
            options.TransportPolicy.Exceptions.Add(new TransportPolicyExceptionConfiguration
            {
                Name = "hyperscale-downgrade",
                Category = TransportPolicyCategories.Diagnostics,
                AppliesTo = { TransportPolicyEndpoints.DiagnosticsHttp },
                Transports = { TransportPolicyTransports.Http2 },
                Encodings = { TransportPolicyEncodings.Json },
                Reason = "Simulated legacy collector"
            });
        }

        return options;
    }
}
