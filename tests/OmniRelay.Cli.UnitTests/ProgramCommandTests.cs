using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using OmniRelay.Cli.UnitTests.Infrastructure;
using OmniRelay.Core.Shards;
using OmniRelay.Core.Shards.ControlPlane;
using OmniRelay.Dispatcher;

namespace OmniRelay.Cli.UnitTests;

public sealed class ProgramCommandTests : CliTestBase
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ServeCommand_InvalidShutdownAfter_ShowsError()
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
    public async ValueTask MeshShardsListCommand_WithFilters_PrintsTable()
    {
        var shard = new ShardSummary(
            "mesh.control",
            "shard-0001",
            "rendezvous",
            "node-a",
            "node-a",
            1.0,
            ShardStatus.Active,
            7,
            "abcd",
            DateTimeOffset.Parse("2024-10-01T00:00:00Z", CultureInfo.InvariantCulture),
            "chg-1");
        var response = new ShardListResponse(new[] { shard }, "cursor-123", 42);
        var json = JsonSerializer.Serialize(response, OmniRelayCliJsonContext.Default.ShardListResponse);
        var handler = new StubHttpMessageHandler(request =>
        {
            request.RequestUri.ShouldNotBeNull();
            request.RequestUri!.Query.ShouldContain("namespace=mesh.control");
            request.RequestUri!.Query.ShouldContain("owner=node-a");
            request.RequestUri!.Query.ShouldContain("status=Active");
            request.Headers.TryGetValues("x-mesh-scope", out var scopes).ShouldBeTrue();
            scopes.ShouldContain("mesh.read");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(handler);

        try
        {
            var harness = new CommandTestHarness(Program.BuildRootCommand());
            var result = await harness.InvokeAsync(
                "mesh",
                "shards",
                "list",
                "--url",
                "http://127.0.0.1:19081",
                "--namespace",
                "mesh.control",
                "--owner",
                "node-a",
                "--status",
                "Active",
                "--search",
                "0001",
                "--page-size",
                "5");

            result.ExitCode.ShouldBe(0);
            result.StdOut.ShouldContain("mesh.control");
            result.StdOut.ShouldContain("shard-0001");
        }
        finally
        {
            CliRuntime.Reset();
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask MeshShardsListCommand_WithInvalidStatus_ExitsEarly()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(handler);
        try
        {
            var harness = new CommandTestHarness(Program.BuildRootCommand());
            var result = await harness.InvokeAsync(
                "mesh",
                "shards",
                "list",
                "--status",
                "bogus");

            result.ExitCode.ShouldBe(1);
            result.StdErr.ShouldContain("Invalid shard status");
            handler.Requests.ShouldBeEmpty();
        }
        finally
        {
            CliRuntime.Reset();
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask MeshShardsDiffCommand_PrintsChanges()
    {
        var shard = new ShardSummary(
            "mesh.control",
            "shard-0900",
            "rendezvous",
            "node-b",
            "node-b",
            1.0,
            ShardStatus.Draining,
            9,
            "ffff",
            DateTimeOffset.Parse("2024-10-02T00:00:00Z", CultureInfo.InvariantCulture),
            "chg-2");
        var diff = new ShardDiffEntry(12, shard, shard with { OwnerNodeId = "node-a" }, new ShardHistoryRecord
        {
            Namespace = shard.Namespace,
            ShardId = shard.ShardId,
            Version = shard.Version,
            StrategyId = shard.StrategyId,
            Actor = "operator",
            Reason = "rebalance",
            ChangeTicket = "chg-2",
            CreatedAt = shard.UpdatedAt,
            OwnerNodeId = shard.OwnerNodeId,
            PreviousOwnerNodeId = "node-a"
        });
        var response = new ShardDiffResponse(new[] { diff }, diff.Position);
        var json = JsonSerializer.Serialize(response, OmniRelayCliJsonContext.Default.ShardDiffResponse);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(handler);

        try
        {
            var harness = new CommandTestHarness(Program.BuildRootCommand());
            var result = await harness.InvokeAsync(
                "mesh",
                "shards",
                "diff",
                "--from-version",
                "1",
                "--to-version",
                "20");

            result.ExitCode.ShouldBe(0);
            result.StdOut.ShouldContain("shard-0900");
            handler.Requests.ShouldHaveSingleItem();
            handler.Requests[0].Headers.TryGetValues("x-mesh-scope", out var scopes).ShouldBeTrue();
            scopes.ShouldContain("mesh.operate");
        }
        finally
        {
            CliRuntime.Reset();
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask MeshShardsSimulateCommand_SendsNodePayload()
    {
        var simulation = new ShardSimulationResponse(
            "mesh.control",
            "rendezvous",
            DateTimeOffset.UtcNow,
            new[]
            {
                new ShardSimulationAssignment("mesh.control", "shard-01", "node-a", 1, null)
            },
            new[]
            {
                new ShardSimulationChange("mesh.control", "shard-01", "node-a", "node-b", true)
            });

        var json = JsonSerializer.Serialize(simulation, OmniRelayCliJsonContext.Default.ShardSimulationResponse);
        var handler = new StubHttpMessageHandler(request =>
        {
            request.Headers.TryGetValues("x-mesh-scope", out var scopes).ShouldBeTrue();
            scopes.ShouldContain("mesh.operate");
            var payload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            payload.ShouldContain("\"nodes\"");
            payload.ShouldContain("node-a");
            payload.ShouldContain("node-b");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(handler);

        try
        {
            var harness = new CommandTestHarness(Program.BuildRootCommand());
            var result = await harness.InvokeAsync(
                "mesh",
                "shards",
                "simulate",
                "--namespace",
                "mesh.control",
                "--strategy",
                "rendezvous",
                "--node",
                "node-a:1.0",
                "--node",
                "node-b:0.8");

            result.ExitCode.ShouldBe(0);
            result.StdOut.ShouldContain("Changes");
        }
        finally
        {
            CliRuntime.Reset();
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ServeCommand_WritesReadyFile_AndStopsHost()
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
    public async ValueTask ConfigValidateCommand_MissingFile_ReturnsError()
    {
        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("config", "validate", "--config", "missing.json");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("does not exist");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ConfigScaffoldCommand_Defaults_EmitsGoldenBaseline()
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
    public async ValueTask ConfigScaffoldCommand_Http3AndOutboundSwitches_EmitsGoldenDocument()
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
    public async ValueTask ConfigScaffoldCommand_WhenWriteFails_ReturnsError()
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
    public async ValueTask IntrospectCommand_PrintsJsonSnapshot()
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
    public async ValueTask RequestCommand_MissingHttpUrl_FailsBeforeNetwork()
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
    public async ValueTask BenchmarkCommand_MissingRequestsAndDuration_ShowsGuidance()
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
    public async ValueTask BenchmarkCommand_WithRequestsAndWarmup_PrintsSummary()
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
    public async ValueTask ScriptRunCommand_ExecutesRequestAndIntrospect()
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
    public async ValueTask ScriptRunCommand_DryRun_SkipsExecution()
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
    public async ValueTask ScriptRunCommand_ContinueOnError_AllowsLaterSteps()
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
    public async ValueTask ScriptCommand_MissingFile_ReturnsError()
    {
        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("script", "run", "--file", "missing.json");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("does not exist");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask MeshStatusCommand_InvalidUrl_ShowsError()
    {
        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("mesh", "leaders", "status", "--url", "invalid");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("Invalid control-plane URL 'invalid'.");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask MeshStatusCommand_Snapshot_PrintsLeaders()
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
    public async ValueTask MeshStatusCommand_Watch_StreamsEvents()
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
    public async ValueTask MeshStatusCommand_WatchTimeout_ReturnsTwo()
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

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask MeshConfigValidateCommand_WithDowngrade_PrintsError()
    {
        var configPath = CreateDiagnosticsConfigFile(enableHttp3: false);
        try
        {
            var harness = new CommandTestHarness(Program.BuildRootCommand());
            var result = await harness.InvokeAsync("mesh", "config", "validate", "--config", configPath);
            result.ExitCode.ShouldBe(0);
            result.StdErr.ShouldBeNullOrEmpty();
            result.StdOut.ShouldContain("Transport policy satisfied", Case.Insensitive);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask MeshConfigValidateCommand_WithOverrides_Succeeds()
    {
        var configPath = CreateDiagnosticsConfigFile(enableHttp3: false);
        try
        {
            var harness = new CommandTestHarness(Program.BuildRootCommand());
            var result = await harness.InvokeAsync(
                "mesh",
                "config",
                "validate",
                "--config",
                configPath,
                "--set",
                "omnirelay:diagnostics:controlPlane:httpRuntime:enableHttp3=true",
                "--set",
                "omnirelay:diagnostics:controlPlane:grpcRuntime:enableHttp3=true");

            result.ExitCode.ShouldBe(0);
            result.StdOut.ShouldContain("Transport policy satisfied", Case.Insensitive);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    private static string CreateConfigFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"omnirelay-cli-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"omnirelay":{}}""");
        return path;
    }

    private static string CreateDiagnosticsConfigFile(bool enableHttp3)
    {
        var path = Path.Combine(Path.GetTempPath(), $"omnirelay-cli-policy-{Guid.NewGuid():N}.json");
        var payload = new
        {
            omnirelay = new
            {
                service = "cli-policy",
                diagnostics = new
                {
                    controlPlane = new
                    {
                        httpUrls = new[] { "https://127.0.0.1:9443" },
                        grpcUrls = new[] { "https://127.0.0.1:9444" },
                        httpRuntime = new { enableHttp3 },
                        grpcRuntime = new { enableHttp3 }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
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
