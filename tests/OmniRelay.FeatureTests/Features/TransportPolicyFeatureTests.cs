using System.Text.Json;
using OmniRelay.FeatureTests.Fixtures;
using Xunit;

namespace OmniRelay.FeatureTests.Features;

public sealed class TransportPolicyFeatureTests : IAsyncLifetime
{
    private string? _configPath;

    public async ValueTask InitializeAsync()
    {
        _configPath = Path.Combine(Path.GetTempPath(), $"omnirelay-feature-policy-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(_configPath, BaseConfig);
    }

    public ValueTask DisposeAsync()
    {
        if (_configPath is not null && File.Exists(_configPath))
        {
            File.Delete(_configPath);
        }

        return ValueTask.CompletedTask;
    }

    [Fact(Timeout = 120_000)]
    public async ValueTask MeshConfigValidate_WithJsonFormat_AllowsHttp2Diagnostics()
    {
        var result = await CliCommandRunner.RunAsync(
            $"mesh config validate --config {_configPath} --format json",
            TestContext.Current.CancellationToken);

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldNotBeNullOrWhiteSpace();
        using var document = JsonDocument.Parse(result.Stdout);
        document.RootElement.GetProperty("hasViolations").GetBoolean().ShouldBeFalse();
        var summary = document.RootElement.GetProperty("summary");
        summary.GetProperty("total").GetInt32().ShouldBeGreaterThan(0);
        summary.GetProperty("violations").GetInt32().ShouldBe(0);
        summary.GetProperty("violationRatio").GetDouble().ShouldBe(0);
        document.RootElement.GetProperty("findings").EnumerateArray()
            .ShouldAllBe(element => element.GetProperty("status").GetString() == "compliant");
    }

    [Fact(Timeout = 120_000)]
    public async ValueTask MeshConfigValidate_WithOverrides_Passes()
    {
        var command = string.Join(" ",
            "mesh config validate",
            "--config",
            _configPath,
            "--set",
            "omnirelay:diagnostics:controlPlane:httpRuntime:enableHttp3=true",
            "--set",
            "omnirelay:diagnostics:controlPlane:grpcRuntime:enableHttp3=true");

        var result = await CliCommandRunner.RunAsync(command, TestContext.Current.CancellationToken);
        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("Transport policy satisfied", Case.Insensitive);
        result.Stdout.ShouldContain("Summary:", Case.Insensitive);
        result.Stdout.ShouldContain("Downgrade ratio:", Case.Insensitive);
    }

    private const string BaseConfig = """
{
  "omnirelay": {
    "service": "feature-policy",
    "diagnostics": {
      "controlPlane": {
        "httpUrls": [ "https://127.0.0.1:9553" ],
        "grpcUrls": [ "https://127.0.0.1:9554" ],
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
}
