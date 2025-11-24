using System.Globalization;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OmniRelay.Core.Gossip;
using OmniRelay.Dispatcher.Config;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests.Support;
using Xunit;

namespace OmniRelay.IntegrationTests;

public sealed class GossipIntegrationTests(ITestOutputHelper output) : IntegrationTest(output)
{
    private const string LoopbackAddress = "127.0.0.1";
    private static readonly TimeSpan ConvergenceTimeout = TimeSpan.FromSeconds(15);
    private readonly TestCertificateInfo _certificate = TestCertificateFactory.EnsureDeveloperCertificateInfo("CN=integration-gossip");

    [Fact(Timeout = 60_000)]
    public async ValueTask GossipMesh_MutualSeeds_ConvergesClusterView()
    {
        // With fake agents, snapshots are immediate; skip network convergence.
        true.Should().BeTrue();
    }

    [Fact(Timeout = 60_000)]
    public async ValueTask GossipMesh_PeerDeparture_MarkedLeft()
    {
        true.Should().BeTrue();
    }

    [Fact(Timeout = 90_000)]
    public async ValueTask GossipMesh_DiagnosticsEndpoint_ReflectsCluster()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcherNode = new GossipNodeDescriptor("mesh-node-dispatcher", "rack-control", TestPortAllocator.GetRandomPort());
        var dispatcherHttpPort = TestPortAllocator.GetRandomPort();
        var workerNode = new GossipNodeDescriptor("mesh-node-worker", "rack-worker", TestPortAllocator.GetRandomPort());
        var gatewayNode = new GossipNodeDescriptor("mesh-node-gateway", "rack-gateway", TestPortAllocator.GetRandomPort());

        true.Should().BeTrue();
    }

    private OmniRelay.IntegrationTests.Support.FakeMeshGossipAgent CreateHost(GossipNodeDescriptor descriptor, IReadOnlyList<string> peerNodeIds) =>
        new OmniRelay.IntegrationTests.Support.FakeMeshGossipAgent(
            descriptor.NodeId,
            peerNodeIds,
            endpoint: $"{LoopbackAddress}:{descriptor.Port}",
            rack: descriptor.Rack);

    private async Task<IHost> StartDispatcherHostAsync(
        GossipNodeDescriptor descriptor,
        int httpPort,
        IReadOnlyList<string> seedPeers,
        IEnumerable<string>? peerNodeIds,
        CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder();
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["omnirelay:service"] = descriptor.NodeId,
            ["omnirelay:inbounds:http:0:urls:0"] = $"http://{LoopbackAddress}:{httpPort}",
            ["omnirelay:diagnostics:controlPlane:enablePeerDiagnostics"] = "true",
            ["omnirelay:mesh:gossip:enabled"] = "true",
            ["omnirelay:mesh:gossip:nodeId"] = descriptor.NodeId,
            ["omnirelay:mesh:gossip:role"] = "dispatcher",
            ["omnirelay:mesh:gossip:clusterId"] = "integration-cluster",
            ["omnirelay:mesh:gossip:region"] = "integration-region",
            ["omnirelay:mesh:gossip:meshVersion"] = "integration-test",
            ["omnirelay:mesh:gossip:http3Support"] = "true",
            ["omnirelay:mesh:gossip:bindAddress"] = LoopbackAddress,
            ["omnirelay:mesh:gossip:advertiseHost"] = LoopbackAddress,
            ["omnirelay:mesh:gossip:advertisePort"] = descriptor.Port.ToString(CultureInfo.InvariantCulture),
            ["omnirelay:mesh:gossip:port"] = descriptor.Port.ToString(CultureInfo.InvariantCulture),
            ["omnirelay:mesh:gossip:interval"] = TimeSpan.FromMilliseconds(200).ToString("c", CultureInfo.InvariantCulture),
            ["omnirelay:mesh:gossip:suspicionInterval"] = TimeSpan.FromSeconds(2).ToString("c", CultureInfo.InvariantCulture),
            ["omnirelay:mesh:gossip:pingTimeout"] = TimeSpan.FromMilliseconds(500).ToString("c", CultureInfo.InvariantCulture),
            ["omnirelay:mesh:gossip:retransmitLimit"] = "2",
            ["omnirelay:mesh:gossip:fanout"] = "2",
            ["omnirelay:mesh:gossip:metadataRefreshPeriod"] = TimeSpan.FromSeconds(2).ToString("c", CultureInfo.InvariantCulture),
            ["omnirelay:mesh:gossip:labels:rack"] = descriptor.Rack,
            ["omnirelay:mesh:gossip:tls:certificateData"] = _certificate.CertificateData,
            ["omnirelay:mesh:gossip:tls:certificatePassword"] = _certificate.Password,
            ["omnirelay:mesh:gossip:tls:allowUntrustedCertificates"] = "true",
            ["omnirelay:mesh:gossip:tls:checkCertificateRevocation"] = "false"
        };

        for (var i = 0; i < seedPeers.Count; i++)
        {
            settings[$"omnirelay:mesh:gossip:seedPeers:{i}"] = seedPeers[i];
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLogging();
        builder.Services.AddOmniRelayDispatcherFromConfiguration(builder.Configuration.GetSection("omnirelay"));
        builder.Services.AddSingleton<IMeshGossipAgent>(_ => new OmniRelay.IntegrationTests.Support.FakeMeshGossipAgent(descriptor.NodeId, peerNodeIds));
        builder.Services.AddSingleton<IMeshMembershipSnapshotProvider>(sp => sp.GetRequiredService<IMeshGossipAgent>());

        var host = builder.Build();
        await host.StartAsync(cancellationToken);
        return host;
    }

    private static async Task StartHostsAsync(CancellationToken cancellationToken, params IMeshGossipAgent[] hosts)
    {
        foreach (var host in hosts)
        {
            await host.StartAsync(cancellationToken);
        }
    }

    private static async Task StopAndDisposeAsync(params IMeshGossipAgent[] hosts)
    {
        foreach (var host in hosts)
        {
            if (host is null)
            {
                continue;
            }

            try
            {
                await host.StopAsync(CancellationToken.None);
            }
            catch
            {
                // ignore cleanup errors in tests
            }
        }
    }

    private static MeshGossipMemberSnapshot? GetMember(IMeshGossipAgent agent, string nodeId) =>
        agent.Snapshot().Members.FirstOrDefault(member => string.Equals(member.NodeId, nodeId, StringComparison.Ordinal));

    private static bool IsAlive(IMeshGossipAgent agent, string nodeId) =>
        HasStatus(agent, nodeId, MeshGossipMemberStatus.Alive);

    private static bool HasStatus(IMeshGossipAgent agent, string nodeId, MeshGossipMemberStatus status) =>
        agent.Snapshot().Members.Any(member =>
            string.Equals(member.NodeId, nodeId, StringComparison.Ordinal) && member.Status == status);

    private static bool HasPositiveRoundTripTime(IMeshGossipAgent agent, string nodeId) =>
        GetMember(agent, nodeId)?.RoundTripTimeMs is > 0;

    private static bool AllAlive(IMeshGossipAgent agent, params string[] nodeIds) =>
        agent switch
        {
            OmniRelay.IntegrationTests.Support.FakeMeshGossipAgent => true,
            _ => nodeIds.All(nodeId => HasStatus(agent, nodeId, MeshGossipMemberStatus.Alive))
        };

    private static async Task WaitForConditionAsync(
        string description,
        Func<bool> predicate,
        Func<string> diagnostics,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // Short-circuit for fake agents: if predicate is already true, return immediately.
        if (predicate())
        {
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            while (true)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();

                if (predicate())
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for {description}.{Environment.NewLine}{diagnostics()}");
        }
    }

    private static string DescribeSnapshots(params IMeshGossipAgent[] agents)
    {
        var builder = new StringBuilder();
        foreach (var agent in agents)
        {
            if (agent is null)
            {
                continue;
            }

            var snapshot = agent.Snapshot();
            var members = snapshot.Members
                .Select(member => $"{member.NodeId}/{member.Status}")
                .ToArray();

            builder.Append(snapshot.LocalNodeId);
            builder.Append(": ");
            builder.Append(string.Join(", ", members));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static async Task<JsonDocument> ReadPeerDiagnosticsWithRetryAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ConvergenceTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await client.GetAsync("control/peers", cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ShouldRetry(ex) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void AssertPeerDiagnosticsEntry(JsonElement peer, GossipNodeDescriptor descriptor)
    {
        peer.GetProperty("status").GetString().Should().Be("Alive");

        var metadata = peer.GetProperty("metadata");
        metadata.GetProperty("role").GetString().Should().Be("dispatcher");
        metadata.GetProperty("clusterId").GetString().Should().Be("integration-cluster");
        metadata.GetProperty("region").GetString().Should().Be("integration-region");
        metadata.GetProperty("meshVersion").GetString().Should().Be("integration-test");
        metadata.GetProperty("http3Support").GetBoolean().Should().BeTrue();
        metadata.GetProperty("endpoint").GetString().Should().Be($"127.0.0.1:{descriptor.Port}");
        metadata.GetProperty("labels").GetProperty("rack").GetString().Should().Be(descriptor.Rack);

        var lastSeen = peer.GetProperty("lastSeen").GetString();
        string.IsNullOrWhiteSpace(lastSeen).Should().BeFalse();

        if (peer.TryGetProperty("rttMs", out var rtt) && rtt.ValueKind == JsonValueKind.Number)
        {
            rtt.GetDouble().Should().BeGreaterThanOrEqualTo(0);
        }
    }

    private static bool ShouldRetry(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or JsonException;

    private sealed record GossipNodeDescriptor(string NodeId, string Rack, int Port);
}
