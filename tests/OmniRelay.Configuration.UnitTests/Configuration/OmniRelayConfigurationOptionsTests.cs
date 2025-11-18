using OmniRelay.Configuration.Models;
using Shouldly;
using Xunit;

namespace OmniRelay.Configuration.UnitTests.Configuration;

public sealed class OmniRelayConfigurationOptionsTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void Defaults_InstantiateChildGraphs()
    {
        var options = new OmniRelayConfigurationOptions();

        options.Inbounds.Http.ShouldBeEmpty();
        options.Inbounds.Grpc.ShouldBeEmpty();
        options.Outbounds.ShouldBeEmpty();
        options.Middleware.Inbound.Unary.ShouldBeEmpty();
        options.Middleware.Outbound.Unary.ShouldBeEmpty();
        options.Logging.Overrides.ShouldBeEmpty();
        options.Encodings.Json.Inbound.ShouldBeEmpty();
        options.Encodings.Json.Outbound.ShouldBeEmpty();
        options.Diagnostics.OpenTelemetry.ShouldNotBeNull();
        options.Diagnostics.Runtime.ShouldNotBeNull();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void OutboundsDictionary_IsCaseInsensitive()
    {
        var options = new OmniRelayConfigurationOptions();
        var outbound = new ServiceOutboundConfiguration();
        options.Outbounds["Primary"] = outbound;

        options.Outbounds["primary"].ShouldBe(outbound);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void LoggingOverrides_IsCaseInsensitive()
    {
        var logging = new LoggingConfiguration();
        logging.Overrides["OmniRelay.Configuration"] = "Trace";

        logging.Overrides["omnirelay.configuration"].ShouldBe("Trace");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void PeerSpecSettings_IsCaseInsensitive()
    {
        var spec = new PeerSpecConfiguration();
        spec.Settings["Mode"] = "sticky";

        spec.Settings["mode"].ShouldBe("sticky");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void JsonCodecRegistration_DefaultsToUnaryKind()
    {
        var codec = new JsonCodecRegistrationConfiguration();
        codec.Kind.ShouldBe("Unary");
        codec.Aliases.ShouldBeEmpty();
    }
}
