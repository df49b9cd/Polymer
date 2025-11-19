using OmniRelay.Configuration.Internal.TransportPolicy;
using OmniRelay.Configuration.Models;
using Shouldly;
using Xunit;

namespace OmniRelay.Configuration.UnitTests.Configuration;

public sealed class TransportPolicyEvaluatorTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void Enforce_WithHttpDiagnosticsDowngrade_Throws()
    {
        var options = CreateBaseOptions();
        options.Diagnostics.ControlPlane.HttpRuntime.EnableHttp3 = false;

        var exception = Should.Throw<OmniRelayConfigurationException>(() => TransportPolicyEvaluator.Enforce(options));
        exception.Message.ShouldContain("diagnostics:http", Case.Insensitive);
        exception.Message.ShouldContain("http2", Case.Insensitive);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Enforce_WithException_AllowsDowngrade()
    {
        var options = CreateBaseOptions();
        options.Diagnostics.ControlPlane.HttpRuntime.EnableHttp3 = false;
        options.TransportPolicy.Exceptions.Add(new TransportPolicyExceptionConfiguration
        {
            Name = "legacy-json",
            Category = TransportPolicyCategories.Diagnostics,
            AppliesTo = { TransportPolicyEndpoints.DiagnosticsHttp },
            Transports = { TransportPolicyTransports.Http2 },
            Encodings = { TransportPolicyEncodings.Json },
            Reason = "Legacy observability stack",
            ExpiresAfter = DateTimeOffset.UtcNow.AddDays(30)
        });

        var result = TransportPolicyEvaluator.Evaluate(options);
        result.HasViolations.ShouldBeFalse();
        result.HasExceptions.ShouldBeTrue();
        result.Findings.ShouldContain(finding => finding.Status == TransportPolicyFindingStatus.Excepted);
        Should.NotThrow(() => TransportPolicyEvaluator.Enforce(options));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Evaluate_WithDowngrade_ComputesSummaryAndHints()
    {
        var options = CreateBaseOptions();
        options.Diagnostics.ControlPlane.HttpRuntime.EnableHttp3 = false;

        var evaluation = TransportPolicyEvaluator.Evaluate(options);

        evaluation.Summary.Total.ShouldBe(2);
        evaluation.Summary.Violations.ShouldBe(1);
        evaluation.Summary.Compliant.ShouldBe(1);
        evaluation.Summary.Excepted.ShouldBe(0);

        var httpFinding = evaluation.Findings.First(finding => finding.Endpoint == TransportPolicyEndpoints.DiagnosticsHttp);
        httpFinding.Http3Enabled.ShouldBeFalse();
        var hint = httpFinding.Hint;
        hint.ShouldNotBeNull();
        hint!.ShouldContain("enableHttp3", Case.Insensitive);
    }

    private static OmniRelayConfigurationOptions CreateBaseOptions()
    {
        var options = new OmniRelayConfigurationOptions
        {
            Service = "policy-tests",
        };

        options.Diagnostics.ControlPlane.HttpUrls.Clear();
        options.Diagnostics.ControlPlane.HttpUrls.Add("https://127.0.0.1:9095");
        options.Diagnostics.ControlPlane.HttpRuntime.EnableHttp3 = true;

        options.Diagnostics.ControlPlane.GrpcUrls.Clear();
        options.Diagnostics.ControlPlane.GrpcUrls.Add("https://127.0.0.1:9096");
        options.Diagnostics.ControlPlane.GrpcRuntime.EnableHttp3 = true;

        return options;
    }
}
