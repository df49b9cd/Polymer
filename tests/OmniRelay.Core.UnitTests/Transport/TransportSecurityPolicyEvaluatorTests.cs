using System.Collections.Immutable;
using System.Security.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.Transport.Security;
using Xunit;

#pragma warning disable SYSLIB0058
namespace OmniRelay.Core.UnitTests.Transport;

public sealed class TransportSecurityPolicyEvaluatorTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void Evaluate_RejectsDisallowedProtocol()
    {
        var policy = new TransportSecurityPolicy(
            enabled: true,
            allowedProtocols: new[] { "http/2" }.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            allowedTlsVersions: ImmutableHashSet<SslProtocols>.Empty,
            allowedCipherAlgorithms: ImmutableHashSet<CipherAlgorithmType>.Empty,
            requireClientCertificate: false,
            allowedThumbprints: ImmutableHashSet<string>.Empty,
            endpointRules: ImmutableArray<TransportEndpointRule>.Empty);

        var evaluator = new TransportSecurityPolicyEvaluator(policy, NullLogger<TransportSecurityPolicyEvaluator>.Instance);
        var context = new TransportSecurityContext
        {
            Transport = "http",
            Protocol = "HTTP/1.1"
        };

        var decision = evaluator.Evaluate(context);
        decision.IsFailure.ShouldBeTrue();
        decision.Error?.Message.ShouldContain("Protocol");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Evaluate_AllowsMatchingEndpointRule()
    {
        var rule = new TransportEndpointRule(true, "*.example.com", null);
        var policy = new TransportSecurityPolicy(
            enabled: true,
            allowedProtocols: ImmutableHashSet<string>.Empty,
            allowedTlsVersions: ImmutableHashSet<SslProtocols>.Empty,
            allowedCipherAlgorithms: ImmutableHashSet<CipherAlgorithmType>.Empty,
            requireClientCertificate: false,
            allowedThumbprints: ImmutableHashSet<string>.Empty,
            endpointRules: ImmutableArray.Create(rule));

        var evaluator = new TransportSecurityPolicyEvaluator(policy, NullLogger<TransportSecurityPolicyEvaluator>.Instance);
        var context = new TransportSecurityContext
        {
            Transport = "http",
            Protocol = "HTTP/2",
            Host = "api.example.com"
        };

        var decision = evaluator.Evaluate(context);
        decision.IsSuccess.ShouldBeTrue();
        decision.Value.IsAllowed.ShouldBeTrue();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Evaluate_RequiresClientCertificate()
    {
        var policy = new TransportSecurityPolicy(
            enabled: true,
            allowedProtocols: ImmutableHashSet<string>.Empty,
            allowedTlsVersions: ImmutableHashSet<SslProtocols>.Empty,
            allowedCipherAlgorithms: ImmutableHashSet<CipherAlgorithmType>.Empty,
            requireClientCertificate: true,
            allowedThumbprints: ImmutableHashSet<string>.Empty,
            endpointRules: ImmutableArray<TransportEndpointRule>.Empty);

        var evaluator = new TransportSecurityPolicyEvaluator(policy, NullLogger<TransportSecurityPolicyEvaluator>.Instance);
        var context = new TransportSecurityContext
        {
            Transport = "http",
            Protocol = "HTTP/2"
        };

        var decision = evaluator.Evaluate(context);
        decision.IsFailure.ShouldBeTrue();
        decision.Error?.Message.ShouldContain("Client certificate required");
    }
}
#pragma warning restore SYSLIB0058
