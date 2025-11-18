using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using OmniRelay.Cli.UnitTests.Infrastructure;
using OmniRelay.Dispatcher;

namespace OmniRelay.Cli.UnitTests;

public sealed class ProgramCommandTests : CliTestBase
{
    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConfigValidateCommand_MissingFile_ReturnsError()
    {
        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("config", "validate", "--config", "missing.json");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("does not exist");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConfigScaffoldCommand_Defaults_EmitsGoldenBaseline()
    {
        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var outputPath = Path.Combine(Path.GetTempPath(), $"omnirelay-scaffold-{Guid.NewGuid():N}.json");

        try
        {
            var result = await harness.InvokeAsync("config", "scaffold", "--output", outputPath);

            result.ExitCode.ShouldBe(0);
            result.StdOut.ShouldContain($"Wrote configuration scaffold to '{outputPath}'.");
            AssertGoldenConfig(outputPath, "baseline.json");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConfigScaffoldCommand_Http3AndOutboundSwitches_EmitsGoldenDocument()
    {
        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var outputPath = Path.Combine(Path.GetTempPath(), $"omnirelay-scaffold-{Guid.NewGuid():N}.json");

        try
        {
            var result = await harness.InvokeAsync(
                "config",
                "scaffold",
                "--output",
                outputPath,
                "--section",
                "fabric",
                "--service",
                "aggregator",
                "--http3-http",
                "--http3-grpc",
                "--include-outbound",
                "--outbound-service",
                "audit");

            result.ExitCode.ShouldBe(0);
            AssertGoldenConfig(outputPath, "http3-outbound.json");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ConfigScaffoldCommand_WhenWriteFails_ReturnsError()
    {
        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"omnirelay-scaffold-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);

        try
        {
            var result = await harness.InvokeAsync("config", "scaffold", "--output", outputDirectory);

            result.ExitCode.ShouldBe(1);
            result.StdErr.ShouldContain("Failed to write scaffold");
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
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

        result.ExitCode.ShouldBe(0, $"StdOut:{Environment.NewLine}{result.StdOut}{Environment.NewLine}StdErr:{Environment.NewLine}{result.StdErr}");
        result.StdOut.ShouldContain("demo-service");
    }

    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task BenchmarkCommand_WithRequestsAndWarmup_PrintsSummary()
    {
        var fakeInvoker = new FakeBenchmarkInvoker(TimeSpan.FromMilliseconds(1));
        BenchmarkRunner.InvokerFactoryOverride = (_, _) => Task.FromResult<BenchmarkRunner.IRequestInvoker>(fakeInvoker);

        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync(
            "benchmark",
            "--transport",
            "http",
            "--service",
            "demo",
            "--procedure",
            "Echo/Call",
            "--url",
            "https://localhost:8443",
            "--body",
            "{}",
            "--requests",
            "5",
            "--concurrency",
            "2",
            "--warmup",
            "00:00:00.02");

        result.ExitCode.ShouldBe(0, $"StdOut:{Environment.NewLine}{result.StdOut}{Environment.NewLine}StdErr:{Environment.NewLine}{result.StdErr}");
        result.StdOut.ShouldContain("Measured requests: 5");
        result.StdOut.ShouldContain("Success: 5");
        fakeInvoker.CallCount.ShouldBeGreaterThan(5);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ScriptRunCommand_ExecutesRequestAndIntrospect()
    {
        var scriptPath = GetAutomationFixture("automation-happy.json");
        var handler = CreateAutomationHandler();
        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(handler);

        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("script", "run", "--file", scriptPath);

        result.ExitCode.ShouldBe(0, $"StdOut:{Environment.NewLine}{result.StdOut}{Environment.NewLine}StdErr:{Environment.NewLine}{result.StdErr}");
        result.StdOut.ShouldContain("Loaded script");
        result.StdOut.ShouldContain("... waiting for");
        handler.Requests.Count.ShouldBe(2);
        handler.Requests.Count(message => message.RequestUri!.AbsoluteUri.Contains("/omnirelay/introspect", StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ScriptRunCommand_DryRun_SkipsExecution()
    {
        var scriptPath = GetAutomationFixture("automation-dryrun.json");
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("dry-run should not invoke network"));
        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(handler);

        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("script", "run", "--file", scriptPath, "--dry-run");

        result.ExitCode.ShouldBe(0, $"StdOut:{Environment.NewLine}{result.StdOut}{Environment.NewLine}StdErr:{Environment.NewLine}{result.StdErr}");
        result.StdOut.ShouldContain("dry-run: skipping request execution.");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ScriptRunCommand_ContinueOnError_AllowsLaterSteps()
    {
        var scriptPath = GetAutomationFixture("automation-continue.json");
        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(CreateAutomationHandler());

        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("script", "run", "--file", scriptPath, "--continue-on-error");

        result.ExitCode.ShouldBe(1, $"StdOut:{Environment.NewLine}{result.StdOut}{Environment.NewLine}StdErr:{Environment.NewLine}{result.StdErr}");
        result.StdErr.ShouldContain("Request step is missing 'service' or 'procedure'.");
        result.StdOut.ShouldContain("[2/2] introspect");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ScriptCommand_MissingFile_ReturnsError()
    {
        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("script", "run", "--file", "missing.json");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("does not exist");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task MeshStatusCommand_InvalidUrl_ShowsError()
    {
        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("mesh", "leaders", "status", "--url", "invalid");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("Invalid control-plane URL 'invalid'.");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task MeshStatusCommand_Snapshot_PrintsLeaders()
    {
        var snapshotJson = """
        {
          "generatedAt": "2024-01-02T03:04:05Z",
          "tokens": [
            {
              "scope": "alpha",
              "scopeKind": "global",
              "leaderId": "node-a",
              "term": 3,
              "fenceToken": 7,
              "issuedAt": "2024-01-02T03:00:00Z",
              "expiresAt": "2024-01-02T03:10:00Z"
            }
          ]
        }
        """;

        var handler = new StubHttpMessageHandler(request =>
        {
            request.Method.ShouldBe(HttpMethod.Get);
            request.RequestUri!.AbsolutePath.ShouldContain("/control/leaders");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(snapshotJson, Encoding.UTF8, "application/json")
            };
        });
        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(handler);

        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("mesh", "leaders", "status", "--url", "https://control.local", "--scope", "alpha");

        result.ExitCode.ShouldBe(0, $"StdOut:{Environment.NewLine}{result.StdOut}{Environment.NewLine}StdErr:{Environment.NewLine}{result.StdErr}");
        result.StdOut.ShouldContain("alpha");
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task MeshStatusCommand_Watch_StreamsEvents()
    {
        var ssePayload = string.Join('\n', new[]
        {
            """data: {"eventKind":"elected","scope":"alpha","occurredAt":"2024-01-02T03:04:05Z","token":{"scope":"alpha","scopeKind":"global","leaderId":"node-a","term":2,"fenceToken":4,"issuedAt":"2024-01-02T03:04:00Z","expiresAt":"2024-01-02T03:06:00Z"}}""",
            "",
            """data: {"eventKind":"revoked","scope":"alpha","occurredAt":"2024-01-02T03:05:05Z","reason":"manual","token":{"scope":"alpha","scopeKind":"global","leaderId":"node-a","term":2,"fenceToken":4,"issuedAt":"2024-01-02T03:04:00Z","expiresAt":"2024-01-02T03:06:00Z"}}""",
            "",
        });

        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(ssePayload)))
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return response;
        });
        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(handler);

        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync(
            "mesh",
            "leaders",
            "status",
            "--url",
            "https://control.local",
            "--watch",
            "--timeout",
            "00:00:01");

        result.ExitCode.ShouldBe(0, $"StdOut:{Environment.NewLine}{result.StdOut}{Environment.NewLine}StdErr:{Environment.NewLine}{result.StdErr}");
        result.StdOut.ShouldContain("Streaming leadership events");
        result.StdOut.ShouldContain("ELECTED");
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task MeshStatusCommand_WatchTimeout_ReturnsTwo()
    {
        var handler = new StubHttpMessageHandler(_ => throw new TaskCanceledException());
        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(handler);

        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync(
            "mesh",
            "leaders",
            "status",
            "--url",
            "https://control.local",
            "--watch",
            "--timeout",
            "00:00:00.10");

        result.ExitCode.ShouldBe(2);
        result.StdErr.ShouldContain("Leadership stream connection timed out.");
    }

    private static string CreateConfigFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"omnirelay-cli-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"omnirelay":{}}""");
        return path;
    }

    private static string GetAutomationFixture(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Automation", fileName);

    private static StubHttpMessageHandler CreateAutomationHandler()
    {
        var snapshot = CreateSampleIntrospection();
        return new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/omnirelay/introspect", StringComparison.OrdinalIgnoreCase))
            {
                var json = JsonSerializer.Serialize(snapshot, OmniRelayCliJsonContext.Default.DispatcherIntrospection);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            };
        });
    }

    private static DispatcherIntrospection CreateSampleIntrospection() =>
        new(
            Service: "automation-demo",
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

    private static void AssertGoldenConfig(string actualPath, string goldenFileName)
    {
        using var actualDocument = JsonDocument.Parse(File.ReadAllText(actualPath));
        using var expectedDocument = JsonDocument.Parse(ReadGoldenFile(goldenFileName));
        var actualJson = Canonicalize(actualDocument.RootElement);
        var expectedJson = Canonicalize(expectedDocument.RootElement);
        actualJson.ShouldBe(expectedJson, $"Golden file '{goldenFileName}' mismatch.");
    }

    private static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        element.WriteTo(writer);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ReadGoldenFile(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ConfigScaffold", fileName);
        File.Exists(path).ShouldBeTrue($"Expected golden file '{path}' to exist.");
        return File.ReadAllText(path);
    }
}
