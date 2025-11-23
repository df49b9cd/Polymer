using OmniRelay.Cli.UnitTests.Infrastructure;

namespace OmniRelay.Cli.UnitTests;

public sealed class ServeModuleTests : CliTestBase
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ServeCommand_StartsHost_WritesReadyFile_Stops()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"serve-config-{Guid.NewGuid():N}.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ConfigScaffold", "baseline.json"), configPath);

        var readyFile = Path.Combine(Path.GetTempPath(), $"ready-{Guid.NewGuid():N}.txt");
        var fakeHostFactory = new FakeServeHostFactory();
        var fakeConsole = new FakeCliConsole();
        var fakeFileSystem = new FakeFileSystem();

        CliRuntime.ServeHostFactory = fakeHostFactory;
        CliRuntime.Console = fakeConsole;
        CliRuntime.FileSystem = fakeFileSystem;

        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync(
            "serve",
            "--config",
            configPath,
            "--section",
            "omnirelay",
            "--ready-file",
            readyFile,
            "--shutdown-after",
            "00:00:00.05");

        result.ExitCode.ShouldBe(0, result.StdErr);
        fakeHostFactory.CreateCount.ShouldBe(1);
        fakeHostFactory.Host.Started.ShouldBeTrue();
        fakeHostFactory.Host.Stopped.ShouldBeTrue();

        fakeFileSystem.EnsuredDirectories.ShouldContain(Path.GetDirectoryName(readyFile));
        fakeFileSystem.Writes.ShouldContain(entry => entry.Path == readyFile, "ready file was not written");
        fakeConsole.Errors.ShouldBeEmpty();
    }
}
