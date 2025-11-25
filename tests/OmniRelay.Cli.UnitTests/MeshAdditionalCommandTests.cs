using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using OmniRelay.Cli.UnitTests.Infrastructure;
using OmniRelay.ControlPlane.Bootstrap;
using OmniRelay.ControlPlane.Upgrade;
using OmniRelay.Core.Gossip;

namespace OmniRelay.Cli.UnitTests;

public sealed class MeshAdditionalCommandTests : CliTestBase
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask MeshPeersListCommand_JsonFormat_WritesSerializedSnapshot()
    {
        var peerResponse = new
        {
            schemaVersion = "v1",
            generatedAt = DateTimeOffset.Parse("2024-10-01T00:00:00Z", CultureInfo.InvariantCulture),
            localNodeId = "node-a",
            peers = new[]
            {
                new
                {
                    nodeId = "node-a",
                    status = MeshGossipMemberStatus.Alive,
                    lastSeen = DateTimeOffset.Parse("2024-10-01T00:00:00Z", CultureInfo.InvariantCulture),
                    rttMs = 0.45,
                    metadata = new
                    {
                        nodeId = "node-a",
                        role = "control",
                        clusterId = "alpha",
                        region = "us-east-1",
                        meshVersion = "1.2.3",
                        http3Support = true,
                        metadataVersion = 7,
                        labels = new Dictionary<string, string> { ["env"] = "test" }
                    }
                },
                new
                {
                    nodeId = "node-b",
                    status = MeshGossipMemberStatus.Suspect,
                    lastSeen = DateTimeOffset.Parse("2024-10-01T00:00:05Z", CultureInfo.InvariantCulture),
                    rttMs = 2.1,
                    metadata = new
                    {
                        nodeId = "node-b",
                        role = "worker",
                        clusterId = "alpha",
                        region = "us-east-1",
                        meshVersion = "1.2.3",
                        http3Support = false,
                        metadataVersion = 3,
                        labels = new Dictionary<string, string>()
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(peerResponse);
        var handler = new StubHttpMessageHandler(request =>
        {
            request.RequestUri!.AbsolutePath.ShouldBe("/control/peers");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(handler);

        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("mesh", "peers", "list", "--url", "http://127.0.0.1:19090", "--format", "json");

        result.ExitCode.ShouldBe(0, result.StdErr);
        result.StdOut.ShouldContain("\"schemaVersion\"");
        result.StdOut.ShouldContain("node-b");
        handler.Requests.ShouldHaveSingleItem();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask MeshUpgradeStatusCommand_AsJson_PrintsSnapshot()
    {
        var snapshot = new NodeDrainSnapshot(
            NodeDrainState.Draining,
            "rolling-upgrade",
            DateTimeOffset.Parse("2025-01-01T05:00:00Z", CultureInfo.InvariantCulture),
            [
                new NodeDrainParticipantSnapshot(
                    "dispatcher",
                    NodeDrainParticipantState.Draining,
                    null,
                    DateTimeOffset.Parse("2025-01-01T05:00:00Z", CultureInfo.InvariantCulture))
            ]);

        var json = JsonSerializer.Serialize(snapshot, OmniRelayCliJsonContext.Default.NodeDrainSnapshot);
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(handler);

        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("mesh", "upgrade", "status", "--json", "--url", "http://127.0.0.1:19901");

        result.ExitCode.ShouldBe(0, result.StdErr);
        result.StdOut.ShouldContain("Draining");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask MeshUpgradeDrainCommand_ForwardsReasonPayload()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            request.Method.ShouldBe(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.ShouldBe("/control/upgrade/drain");
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            body.ShouldContain("\"reason\":\"safety-check\"");

            var snapshot = new NodeDrainSnapshot(
                NodeDrainState.Draining,
                "safety-check",
                DateTimeOffset.UtcNow,
                [new NodeDrainParticipantSnapshot("dispatcher", NodeDrainParticipantState.Draining, null, DateTimeOffset.UtcNow)]);
            var json = JsonSerializer.Serialize(snapshot, OmniRelayCliJsonContext.Default.NodeDrainSnapshot);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(handler);

        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("mesh", "upgrade", "drain", "--reason", "safety-check", "--url", "http://127.0.0.1:19902");

        result.ExitCode.ShouldBe(0, result.StdErr);
        result.StdOut.ShouldContain("State   : Draining");
        result.StdOut.ShouldContain("safety-check");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask MeshUpgradeResumeCommand_Timeout_ReturnsTwo()
    {
        var handler = new StubHttpMessageHandler(_ => throw new TaskCanceledException("simulated timeout"));
        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(handler);

        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("mesh", "upgrade", "resume", "--url", "http://127.0.0.1:19903");

        result.ExitCode.ShouldBe(2);
        result.StdErr.ShouldContain("Resume request timed out.");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask MeshBootstrapJoinCommand_WritesBundleToFile()
    {
        var joinResponse = new BootstrapJoinResponse
        {
            ClusterId = "alpha",
            Role = "worker",
            Identity = "node-a",
            IdentityProvider = "tests",
            CertificateData = "BASE64CERT",
            TrustBundleData = "ROOT",
            SeedPeers = ["https://peer-a:8443", "https://peer-b:8443"],
            IssuedAt = DateTimeOffset.Parse("2025-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            RenewAfter = DateTimeOffset.Parse("2025-01-02T00:00:00Z", CultureInfo.InvariantCulture),
            ExpiresAt = DateTimeOffset.Parse("2025-01-03T00:00:00Z", CultureInfo.InvariantCulture)
        };

        var json = JsonSerializer.Serialize(joinResponse, BootstrapJsonContext.Default.BootstrapJoinResponse);
        var handler = new StubHttpMessageHandler(request =>
        {
            request.Method.ShouldBe(HttpMethod.Post);
            request.RequestUri!.AbsolutePath.ShouldBe("/omnirelay/bootstrap/join");
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            body.ShouldContain("token-123");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });
        CliRuntime.HttpClientFactory = new FakeHttpClientFactory(handler);

        var outputPath = Path.Combine(Path.GetTempPath(), $"bootstrap-{Guid.NewGuid():N}.json");
        try
        {
            var harness = new CommandTestHarness(Program.BuildRootCommand());
            var result = await harness.InvokeAsync("mesh", "bootstrap", "join", "--url", "http://127.0.0.1:19200", "--token", "token-123", "--output", outputPath);

            result.ExitCode.ShouldBe(0, result.StdErr);
            File.Exists(outputPath).ShouldBeTrue();
            var written = File.ReadAllText(outputPath);
            written.ShouldContain("alpha");
            written.ShouldContain("peer-b");
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
    public async ValueTask MeshBootstrapIssueCommand_GeneratesToken()
    {
        var harness = new CommandTestHarness(Program.BuildRootCommand());
        var result = await harness.InvokeAsync("mesh", "bootstrap", "issue-token", "--signing-key", "secret-key", "--cluster", "alpha", "--role", "worker", "--lifetime", "30m", "--max-uses", "5", "--issuer", "tests");

        result.ExitCode.ShouldBe(0, result.StdErr);
        result.StdOut.ShouldNotBeNullOrWhiteSpace();
        result.StdOut.Trim().Length.ShouldBeGreaterThan(16);
    }
}
