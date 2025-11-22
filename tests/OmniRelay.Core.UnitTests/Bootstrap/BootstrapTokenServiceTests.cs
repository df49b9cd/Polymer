using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.ControlPlane.Bootstrap;
using Xunit;

namespace OmniRelay.Core.UnitTests.Bootstrap;

public sealed class BootstrapTokenServiceTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void CreateAndValidateToken_Succeeds()
    {
        var service = CreateService();
        var descriptor = new BootstrapTokenDescriptor
        {
            ClusterId = "cluster-a",
            Role = "worker",
            Lifetime = TimeSpan.FromMinutes(5)
        };

        var token = service.CreateToken(descriptor);
        var result = service.ValidateToken(token, "cluster-a");

        result.IsValid.ShouldBeTrue();
        result.ClusterId.ShouldBe("cluster-a");
        result.Role.ShouldBe("worker");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ValidateToken_FailsWhenReused()
    {
        var service = CreateService(maxUses: 1);
        var descriptor = new BootstrapTokenDescriptor { ClusterId = "cluster-b", Role = "admin", Lifetime = TimeSpan.FromMinutes(5) };
        var token = service.CreateToken(descriptor);

        service.ValidateToken(token, "cluster-b").IsValid.ShouldBeTrue();
        service.ValidateToken(token, "cluster-b").IsValid.ShouldBeFalse();
    }

    private static BootstrapTokenService CreateService(int? maxUses = null)
    {
        var signingOptions = new BootstrapTokenSigningOptions
        {
            SigningKey = Encoding.UTF8.GetBytes("signing-key"),
            Issuer = "test-suite",
            DefaultLifetime = TimeSpan.FromMinutes(10),
            DefaultMaxUses = maxUses
        };
        var replay = new InMemoryBootstrapReplayProtector();
        return new BootstrapTokenService(signingOptions, replay, NullLogger<BootstrapTokenService>.Instance);
    }
}
