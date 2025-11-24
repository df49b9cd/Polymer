using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using OmniRelay.Dispatcher;
using OmniRelay.IntegrationTests.Support;
using Xunit;

namespace OmniRelay.IntegrationTests.Cli;

public class IntrospectAndPeersIntegrationTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask IntrospectCommand_PrintsTextSnapshot()
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };

        var port = TestPortAllocator.GetRandomPort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        var app = builder.Build();

        var snapshot = new DispatcherIntrospection(
            Service: "demo-int",
            Status: DispatcherStatus.Running,
            Procedures: new ProcedureGroups([], [], [], [], []),
            Components: [],
            Outbounds: [],
            Middleware: new MiddlewareSummary([], [], [], [], [], [], [], [], [], []));

        app.MapGet("/omnirelay/introspect", () => Results.Json(snapshot, jsonOptions));

        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var result = await OmniRelayCliTestHelper.RunAsync(
                new[] { "introspect", "--url", $"http://127.0.0.1:{port}/omnirelay/introspect", "--format", "text" },
                TestContext.Current.CancellationToken);

            result.ExitCode.Should().Be(0);
            result.StandardOutput.Should().Contain("Service: demo-int");
            result.StandardOutput.Should().Contain("Status: Running");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask MeshPeersList_CommandJsonFormat_Succeeds()
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };

        var port = TestPortAllocator.GetRandomPort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        var app = builder.Build();

        var peersPayload = new
        {
            schemaVersion = "v1",
            generatedAt = DateTimeOffset.UtcNow,
            localNodeId = "node-a",
            peers = new[]
            {
                new
                {
                    nodeId = "node-a",
                    status = OmniRelay.Core.Gossip.MeshGossipMemberStatus.Alive,
                    lastSeen = DateTimeOffset.UtcNow,
                    rttMs = 0.42,
                    metadata = new
                    {
                        nodeId = "node-a",
                        role = "control",
                        clusterId = "alpha",
                        region = "us-east-1",
                        meshVersion = "1.2.3",
                        http3Support = true
                    }
                },
                new
                {
                    nodeId = "node-b",
                    status = OmniRelay.Core.Gossip.MeshGossipMemberStatus.Suspect,
                    lastSeen = DateTimeOffset.UtcNow.AddSeconds(-5),
                    rttMs = 1.1,
                    metadata = new
                    {
                        nodeId = "node-b",
                        role = "worker",
                        clusterId = "alpha",
                        region = "us-east-1",
                        meshVersion = "1.2.3",
                        http3Support = false
                    }
                }
            }
        };

        app.MapGet("/control/peers", () => Results.Json(peersPayload, jsonOptions));

        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var result = await OmniRelayCliTestHelper.RunAsync(
                new[] { "mesh", "peers", "list", "--url", $"http://127.0.0.1:{port}", "--format", "json" },
                TestContext.Current.CancellationToken);

            result.ExitCode.Should().Be(0);
            result.StandardOutput.Should().Contain("node-b");
            result.StandardOutput.Should().ContainEquivalentOf("schemaVersion");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}
