using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests.Support;
using Xunit;

namespace OmniRelay.IntegrationTests;

[Collection("CLI Integration")]
public sealed class TransportPolicyIntegrationTests
{
    private const string BaseConfig = """
{
  "omnirelay": {
    "service": "cli-policy",
    "diagnostics": {
      "controlPlane": {
        "httpUrls": [ "https://127.0.0.1:9443" ],
        "grpcUrls": [ "https://127.0.0.1:9444" ],
        "httpRuntime": {
          "enableHttp3": false
        },
        "grpcRuntime": {
          "enableHttp3": false
        }
      }
    }
  }
}
""";

    [Fact(Timeout = 120_000)]
    public async ValueTask MeshConfigValidate_AllowsHttp2DiagnosticsByDefault()
    {
        using var tempDir = new TempDirectory();
        var configPath = TempDirectory.Resolve("policy.appsettings.json");
        await File.WriteAllTextAsync(configPath, BaseConfig, TestContext.Current.CancellationToken);

        var result = await OmniRelayCliTestHelper.RunAsync(
            ["mesh", "config", "validate", "--config", configPath],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("policy violations", result.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Transport policy satisfied", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Summary:", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 120_000)]
    public async ValueTask MeshConfigValidate_AllowsHttp3ViaOverrides()
    {
        using var tempDir = new TempDirectory();
        var configPath = TempDirectory.Resolve("policy.appsettings.json");
        await File.WriteAllTextAsync(configPath, BaseConfig, TestContext.Current.CancellationToken);

        var args = new List<string>
        {
            "mesh",
            "config",
            "validate",
            "--config",
            configPath,
            "--set",
            "omnirelay:diagnostics:controlPlane:httpRuntime:enableHttp3=true",
            "--set",
            "omnirelay:diagnostics:controlPlane:grpcRuntime:enableHttp3=true"
        };

        var result = await OmniRelayCliTestHelper.RunAsync(args, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Transport policy satisfied", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Summary:", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Downgrade ratio", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }
}
