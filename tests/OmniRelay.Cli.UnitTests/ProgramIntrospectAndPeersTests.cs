using System.Net;
using System.Text;
using System.Text.Json;
using OmniRelay.Cli.UnitTests.Infrastructure;
using OmniRelay.Core.Gossip;
using OmniRelay.Dispatcher;

namespace OmniRelay.Cli.UnitTests;

public sealed class ProgramIntrospectAndPeersTests : CliTestBase
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask IntrospectCommand_WithUnknownFormat_ReturnsError()
    {
        var snapshot = new DispatcherIntrospection(
            Service: "demo",
            Status: DispatcherStatus.Running,
            Procedures: new ProcedureGroups(
                [], [], [], [], []),
            Components: [],
            Outbounds: [],
            Middleware: new MiddlewareSummary([], [], [], [], [], [], [], [], [], []));

        var json = JsonSerializer.Serialize(snapshot, OmniRelayCliJsonContext.Default.DispatcherIntrospection);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(handler);

        try
        {
            var harness = new CommandTestHarness(Program.BuildRootCommand());
            var result = await harness.InvokeAsync("introspect", "--format", "xml");

            result.ExitCode.ShouldBe(1);
            result.StdErr.ShouldContain("Unknown format", Case.Insensitive);
        }
        finally
        {
            CliRuntime.Reset();
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask IntrospectCommand_InvalidTimeout_ExitsEarly()
    {
        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("introspect", "--timeout", "not-a-timespan");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("Could not parse timeout", Case.Insensitive);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask MeshPeersCommand_JsonFormat_PrintsJson()
    {
        var payload = new
        {
            schemaVersion = "v1",
            generatedAt = DateTimeOffset.UtcNow,
            localNodeId = "node-a",
            peers = new[]
            {
                new
                {
                    nodeId = "node-a",
                    status = MeshGossipMemberStatus.Alive,
                    lastSeen = DateTimeOffset.UtcNow,
                    rttMs = 0.75,
                    metadata = new
                    {
                        nodeId = "node-a",
                        role = "control",
                        clusterId = "alpha",
                        meshVersion = "1.0.0",
                        http3Support = true
                    }
                },
                new
                {
                    nodeId = "node-b",
                    status = MeshGossipMemberStatus.Suspect,
                    lastSeen = DateTimeOffset.UtcNow.AddSeconds(-3),
                    rttMs = 1.2,
                    metadata = new
                    {
                        nodeId = "node-b",
                        role = "worker",
                        clusterId = "alpha",
                        meshVersion = "1.0.0",
                        http3Support = false
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(handler);

        try
        {
            var harness = new CommandTestHarness(Program.BuildRootCommand());
            var result = await harness.InvokeAsync("mesh", "peers", "list", "--format", "json");

            result.ExitCode.ShouldBe(0);
            result.StdOut.ShouldContain("node-b");
            result.StdOut.ShouldContain("schemaVersion", Case.Insensitive);
        }
        finally
        {
            CliRuntime.Reset();
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask MeshPeersCommand_InvalidTimeout_ReturnsError()
    {
        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("mesh", "peers", "list", "--timeout", "not-a-duration");

        result.ExitCode.ShouldBe(1);

        result.StdErr.ShouldContain("Could not parse timeout", Case.Insensitive);
    }
}
