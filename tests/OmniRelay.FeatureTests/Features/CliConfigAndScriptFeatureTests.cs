using OmniRelay.FeatureTests.Fixtures;
using Xunit;

namespace OmniRelay.FeatureTests.Features;

[Collection(nameof(FeatureTestCollection))]
public sealed class CliConfigAndScriptFeatureTests
{
    private readonly FeatureTestApplication _application;

    public CliConfigAndScriptFeatureTests(FeatureTestApplication application)
    {
        _application = application; // ensures fixture is initialized for CLI invocations.
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ConfigScaffoldThenValidate_Succeeds()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"omnirelay-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "appsettings.json");

        try
        {
            var scaffold = await CliCommandRunner.RunAsync(
                $"config scaffold --output {configPath} --section omnirelay --service feature-cli",
                TestContext.Current.CancellationToken);

            scaffold.ExitCode.ShouldBe(0, scaffold.Stdout + scaffold.Stderr);
            File.Exists(configPath).ShouldBeTrue();

            var validate = await CliCommandRunner.RunAsync(
                $"config validate --config {configPath} --section omnirelay",
                TestContext.Current.CancellationToken);

            validate.ExitCode.ShouldBe(0, validate.Stdout + validate.Stderr);
            validate.Stdout.ShouldContain("Configuration valid", Case.Insensitive);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ScriptRun_DryRun_CoversRequestAndIntrospect()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"omnirelay-script-{Guid.NewGuid():N}.json");
        var script = """
        {
          "steps": [
            { "type": "request", "transport": "http", "service": "orders", "procedure": "Echo/Call", "url": "http://127.0.0.1:0", "body": "{}" },
            { "type": "introspect", "url": "http://127.0.0.1:0", "format": "text" },
            { "type": "delay", "duration": "00:00:00.1" }
          ]
        }
        """;
        await File.WriteAllTextAsync(scriptPath, script, TestContext.Current.CancellationToken);

        try
        {
            var result = await CliCommandRunner.RunAsync(
                $"script run --file {scriptPath} --dry-run --continue-on-error",
                TestContext.Current.CancellationToken);

            result.ExitCode.ShouldBe(0, result.Stdout + result.Stderr);
            result.Stdout.ShouldContain("dry-run: skipping request execution", Case.Insensitive);
            result.Stdout.ShouldContain("Loaded script", Case.Insensitive);
        }
        finally
        {
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }
        }
    }
}
