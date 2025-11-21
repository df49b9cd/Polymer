using OmniRelay.Cli.UnitTests.Infrastructure;

namespace OmniRelay.Cli.UnitTests;

public sealed class ScriptModuleTests : CliTestBase
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ScriptRunCommand_InvalidJson_ReturnsError()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"omnirelay-script-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(scriptPath, "{\"steps\":[invalid}");

        try
        {
            var harness = new CommandTestHarness(Program.BuildRootCommand());
            var result = await harness.InvokeAsync("script", "run", "--file", scriptPath);

            result.ExitCode.ShouldBe(1);
            result.StdErr.ShouldContain("Failed to parse script", Case.Insensitive);
        }
        finally
        {
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ScriptRunCommand_EmptySteps_ReturnsError()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"omnirelay-script-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(scriptPath, "{\"steps\":[]}");

        try
        {
            var harness = new CommandTestHarness(Program.BuildRootCommand());
            var result = await harness.InvokeAsync("script", "run", "--file", scriptPath);

            result.ExitCode.ShouldBe(1);
            result.StdErr.ShouldContain("does not contain any steps", Case.Insensitive);
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
