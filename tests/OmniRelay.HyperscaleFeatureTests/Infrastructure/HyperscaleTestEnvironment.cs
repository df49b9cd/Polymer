using Microsoft.Extensions.Configuration;
using OmniRelay.Tests.Support;

namespace OmniRelay.HyperscaleFeatureTests.Infrastructure;

internal static class HyperscaleTestEnvironment
{
    private static readonly Lazy<TestCertificateInfo> CertificateLazy = new(() =>
        TestCertificateFactory.EnsureDeveloperCertificateInfo("CN=OmniRelay.HyperscaleTests"));

    public static TestCertificateInfo Certificate => CertificateLazy.Value;

    public static IConfigurationBuilder AddDefaultOmniRelayConfiguration(this IConfigurationBuilder builder)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        var tlsDefaults = TestCertificateConfiguration.BuildTlsDefaults(Certificate, "Hyperscale");
        return builder.AddInMemoryCollection(tlsDefaults);
    }
}
