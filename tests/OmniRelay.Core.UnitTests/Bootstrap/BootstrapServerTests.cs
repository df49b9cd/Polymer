using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.ControlPlane.Bootstrap;
using Xunit;

namespace OmniRelay.Core.UnitTests.Bootstrap;

public sealed class BootstrapServerTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask JoinAsync_ReturnsBundleWithCertificate()
    {
        var signingOptions = new BootstrapTokenSigningOptions
        {
            SigningKey = Encoding.UTF8.GetBytes("bundle-signing-key"),
            Issuer = "test-suite",
            DefaultLifetime = TimeSpan.FromMinutes(10)
        };
        var tokenService = new BootstrapTokenService(signingOptions, new InMemoryBootstrapReplayProtector(), NullLogger<BootstrapTokenService>.Instance);

        var serverOptions = new BootstrapServerOptions
        {
            ClusterId = "cluster-1",
            DefaultRole = "worker",
            BundlePassword = "bundle-pass"
        };
        serverOptions.SeedPeers.Add("https://seed-a:8080");
        serverOptions.SeedPeers.Add("https://seed-b:8080");

        var certificateBytes = CreateCertificateBytes("CN=bootstrap-test", "bundle-pass");
        var identityProvider = new TestWorkloadIdentityProvider(certificateBytes, "bundle-pass");
        var policyDocument = new BootstrapPolicyDocument("allow-all", true, Array.Empty<BootstrapPolicyRule>());
        var policyEvaluator = new BootstrapPolicyEvaluator([policyDocument], requireAttestation: false, TimeSpan.FromMinutes(5), NullLogger<BootstrapPolicyEvaluator>.Instance);
        var server = new BootstrapServer(serverOptions, tokenService, identityProvider, policyEvaluator, NullLogger<BootstrapServer>.Instance);

        var token = tokenService.CreateToken(new BootstrapTokenDescriptor { ClusterId = "cluster-1", Role = "worker" });
        var result = await server.JoinAsync(new BootstrapJoinRequest { Token = token }, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        var response = result.ValueOrChecked();

        response.ClusterId.ShouldBe("cluster-1");
        response.Role.ShouldBe("worker");
        response.SeedPeers.ShouldContain("https://seed-a:8080");
        response.CertificateData.ShouldNotBeNullOrWhiteSpace();
    }

    private static byte[] CreateCertificateBytes(string subject, string password)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return certificate.Export(X509ContentType.Pfx, password);
    }

    private sealed class TestWorkloadIdentityProvider : IWorkloadIdentityProvider
    {
        private readonly byte[] _data;
        private readonly string? _password;

        public TestWorkloadIdentityProvider(byte[] data, string? password)
        {
            _data = data;
            _password = password;
        }

        public ValueTask<WorkloadCertificateBundle> IssueAsync(WorkloadIdentityRequest request, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return ValueTask.FromResult(new WorkloadCertificateBundle
            {
                Identity = request.IdentityHint ?? $"test:{request.NodeId}",
                Provider = "test",
                CertificateData = (byte[])_data.Clone(),
                CertificatePassword = _password,
                TrustBundleData = null,
                Metadata = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                IssuedAt = now,
                RenewAfter = now,
                ExpiresAt = now.AddMinutes(5)
            });
        }

        public ValueTask<WorkloadCertificateBundle> RenewAsync(WorkloadIdentityRequest request, CancellationToken cancellationToken = default) => IssueAsync(request, cancellationToken);

        public ValueTask RevokeAsync(string identity, string? reason = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
