using System.Text.Json;
using OmniRelay.Cli.UnitTests.Infrastructure;

namespace OmniRelay.Cli.UnitTests;

public sealed class ConfigCommandsModuleTests : CliTestBase
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ValidateCommand_WithService_PrintsServiceName()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"cli-config-{Guid.NewGuid():N}.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ConfigScaffold", "baseline.json"), tempFile);

        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("config", "validate", "--config", tempFile, "--section", "omnirelay");

        result.ExitCode.ShouldBe(0, result.StdErr);
        result.StdOut.ShouldContain("Configuration valid for service 'sample'", Case.Insensitive);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ScaffoldCommand_IncludesOutboundAndHttp3_WhenRequested()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"cli-scaffold-{Guid.NewGuid():N}.json");

        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync(
            "config",
            "scaffold",
            "--output",
            tempFile,
            "--section",
            "demo",
            "--service",
            "cart",
            "--http3-http",
            "--http3-grpc",
            "--include-outbound",
            "--outbound-service",
            "search");

        result.ExitCode.ShouldBe(0, result.StdErr);
        File.Exists(tempFile).ShouldBeTrue();

        using var document = JsonDocument.Parse(File.ReadAllText(tempFile));
        var root = document.RootElement.GetProperty("demo");
        var inbounds = root.GetProperty("inbounds");
        inbounds.GetProperty("http").EnumerateArray().ShouldContain(
            element => element.TryGetProperty("runtime", out var runtime) &&
                       runtime.TryGetProperty("enableHttp3", out var h3) &&
                       h3.GetBoolean());
        inbounds.GetProperty("grpc").EnumerateArray().ShouldContain(
            element => element.TryGetProperty("runtime", out var runtime) &&
                       runtime.TryGetProperty("enableHttp3", out var h3) &&
                       h3.GetBoolean());

        var outbounds = root.GetProperty("outbounds");
        outbounds.TryGetProperty("search", out var outbound).ShouldBeTrue();
        var endpoints = outbound.GetProperty("unary").GetProperty("grpc")[0].GetProperty("endpoints");
        endpoints.EnumerateArray().ShouldContain(e => e.TryGetProperty("supportsHttp3", out var flag) && flag.GetBoolean());
    }
}
