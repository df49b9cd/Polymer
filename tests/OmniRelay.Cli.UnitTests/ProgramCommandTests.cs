using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using OmniRelay.Cli;
using OmniRelay.Cli.UnitTests.Infrastructure;
using OmniRelay.Dispatcher;

namespace OmniRelay.Cli.UnitTests;

public sealed class ProgramCommandTests : CliTestBase
{
    [Fact]
    public async Task ServeCommand_InvalidShutdownAfter_ShowsError()
    {
        var configPath = CreateConfigFile();
        try
        {
            var harness = new CommandTestHarness(Program.BuildRootCommand());
            var result = await harness.InvokeAsync("serve", "--config", configPath, "--shutdown-after", "not-a-duration");

            result.ExitCode.ShouldBe(1);
            result.StdErr.ShouldContain("Could not parse --shutdown-after value 'not-a-duration'.");
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task ServeCommand_WritesReadyFile_AndStopsHost()
    {
        var configPath = CreateConfigFile();
        var readyFile = Path.Combine(Path.GetTempPath(), $"ready-{Guid.NewGuid():N}.txt");
        var fakeHostFactory = new FakeServeHostFactory();
        var fakeFileSystem = new FakeFileSystem();
        CliRuntime.ServeHostFactory = fakeHostFactory;
        CliRuntime.FileSystem = fakeFileSystem;

        try
        {
            var harness = new CommandTestHarness(Program.BuildRootCommand());
            var result = await harness.InvokeAsync(
                    "serve",
                    "--config",
                    configPath,
                    "--ready-file",
                    readyFile,
                    "--shutdown-after",
                    "00:00:00.05")
                ;

            result.ExitCode.ShouldBe(0);
            fakeHostFactory.CreateCount.ShouldBe(1);
            fakeHostFactory.Host.Started.ShouldBeTrue();
            fakeHostFactory.Host.Stopped.ShouldBeTrue();
            fakeFileSystem.Writes.ShouldContain(tuple => tuple.Path == readyFile);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task ConfigValidateCommand_MissingFile_ReturnsError()
    {
        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("config", "validate", "--config", "missing.json");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("does not exist");
    }

    [Fact]
    public async Task IntrospectCommand_PrintsJsonSnapshot()
    {
        var snapshot = new DispatcherIntrospection(
            Service: "demo-service",
            Status: DispatcherStatus.Running,
            Procedures: new ProcedureGroups(
                ImmutableArray<ProcedureDescriptor>.Empty,
                ImmutableArray<ProcedureDescriptor>.Empty,
                ImmutableArray<StreamProcedureDescriptor>.Empty,
                ImmutableArray<ClientStreamProcedureDescriptor>.Empty,
                ImmutableArray<DuplexProcedureDescriptor>.Empty),
            Components: ImmutableArray<LifecycleComponentDescriptor>.Empty,
            Outbounds: ImmutableArray<OutboundDescriptor>.Empty,
            Middleware: new MiddlewareSummary(
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty));

        var json = JsonSerializer.Serialize(snapshot, OmniRelayCliJsonContext.Default.DispatcherIntrospection);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });
        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(handler);

        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("introspect", "--url", "http://localhost:9000/omnirelay/introspect", "--format", "json");

        result.ExitCode.ShouldBe(0);
        result.StdOut.ShouldContain("demo-service");
    }

    [Fact]
    public async Task RequestCommand_MissingHttpUrl_FailsBeforeNetwork()
    {
        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync(
            "request",
            "--transport",
            "http",
            "--service",
            "demo",
            "--procedure",
            "Echo/Call");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("HTTP transport requires --url");
    }

    [Fact]
    public async Task BenchmarkCommand_MissingRequestsAndDuration_ShowsGuidance()
    {
        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync(
            "benchmark",
            "--transport",
            "http",
            "--service",
            "demo",
            "--procedure",
            "Echo/Call",
            "--requests",
            "0",
            "--url",
            "http://localhost:8080");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("Specify a positive --requests value or a --duration");
    }

    [Fact]
    public async Task ScriptCommand_MissingFile_ReturnsError()
    {
        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("script", "run", "--file", "missing.json");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("does not exist");
    }

    [Fact]
    public async Task MeshStatusCommand_InvalidUrl_ShowsError()
    {
        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("mesh", "leaders", "status", "--url", "invalid");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("Invalid control-plane URL 'invalid'.");
    }

    private static string CreateConfigFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"omnirelay-cli-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"polymer":{}}""");
        return path;
    }
}
