using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniRelay.Configuration;
using OmniRelay.Core.Gossip;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests.Support;
using Xunit;

namespace OmniRelay.IntegrationTests;

public sealed class GossipIntegrationTests(ITestOutputHelper output) : IntegrationTest(output)
{
    private const string LoopbackAddress = "127.0.0.1";
    private static readonly TimeSpan ConvergenceTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DepartureTimeout = TimeSpan.FromSeconds(20);
    private readonly TestCertificateInfo _certificate = TestCertificateFactory.EnsureDeveloperCertificateInfo("CN=integration-gossip");

    [Fact(Timeout = 60_000)]
    public async Task GossipMesh_MutualSeeds_ConvergesClusterView()
    {
        var ct = TestContext.Current.CancellationToken;
        var nodeA = new GossipNodeDescriptor("mesh-node-a", "rack-a", TestPortAllocator.GetRandomPort());
        var nodeB = new GossipNodeDescriptor("mesh-node-b", "rack-b", TestPortAllocator.GetRandomPort());

        var hostA = CreateHost(nodeA, [$"{LoopbackAddress}:{nodeB.Port}"]);
        var hostB = CreateHost(nodeB, [$"{LoopbackAddress}:{nodeA.Port}"]);

        await StartHostsAsync(ct, hostA, hostB);

        try
        {
            await WaitForConditionAsync(
                "gossip cluster convergence",
                () => IsAlive(hostA, nodeB.NodeId) && IsAlive(hostB, nodeA.NodeId),
                () => DescribeSnapshots(hostA, hostB),
                ConvergenceTimeout,
                ct);

            await WaitForConditionAsync(
                "gossip round-trip measurement",
                () => HasPositiveRoundTripTime(hostA, nodeB.NodeId) && HasPositiveRoundTripTime(hostB, nodeA.NodeId),
                () => DescribeSnapshots(hostA, hostB),
                ConvergenceTimeout,
                ct);

            var memberSeenByA = GetMember(hostA, nodeB.NodeId)!;
            var memberSeenByB = GetMember(hostB, nodeA.NodeId)!;

            Assert.Equal(MeshGossipMemberStatus.Alive, memberSeenByA.Status);
            Assert.Equal($"127.0.0.1:{nodeB.Port}", memberSeenByA.Metadata.Endpoint);
            Assert.Equal("integration-cluster", memberSeenByA.Metadata.ClusterId);
            Assert.Equal("dispatcher", memberSeenByA.Metadata.Role);
            Assert.Equal("rack-b", memberSeenByA.Metadata.Labels["rack"]);
            Assert.True(memberSeenByA.RoundTripTimeMs is > 0);

            Assert.Equal(MeshGossipMemberStatus.Alive, memberSeenByB.Status);
            Assert.Equal($"127.0.0.1:{nodeA.Port}", memberSeenByB.Metadata.Endpoint);
            Assert.Equal("rack-a", memberSeenByB.Metadata.Labels["rack"]);
        }
        finally
        {
            await StopAndDisposeAsync(hostA, hostB);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task GossipMesh_PeerDeparture_MarkedLeft()
    {
        var ct = TestContext.Current.CancellationToken;
        var nodeA = new GossipNodeDescriptor("mesh-node-a", "rack-a", TestPortAllocator.GetRandomPort());
        var nodeB = new GossipNodeDescriptor("mesh-node-b", "rack-b", TestPortAllocator.GetRandomPort());

        var hostA = CreateHost(nodeA, [$"{LoopbackAddress}:{nodeB.Port}"]);
        var hostB = CreateHost(nodeB, [$"{LoopbackAddress}:{nodeA.Port}"]);

        await StartHostsAsync(ct, hostA, hostB);

        try
        {
            await WaitForConditionAsync(
                "gossip cluster convergence",
                () => IsAlive(hostA, nodeB.NodeId) && IsAlive(hostB, nodeA.NodeId),
                () => DescribeSnapshots(hostA, hostB),
                ConvergenceTimeout,
                ct);

            await StopAndDisposeAsync(hostB);

            await WaitForConditionAsync(
                "peer departure propagation",
                () => HasStatus(hostA, nodeB.NodeId, MeshGossipMemberStatus.Left),
                () => DescribeSnapshots(hostA),
                DepartureTimeout,
                ct);
        }
        finally
        {
            await StopAndDisposeAsync(hostA, hostB);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task GossipMesh_DiagnosticsEndpoint_ReflectsCluster()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcherNode = new GossipNodeDescriptor("mesh-node-dispatcher", "rack-control", TestPortAllocator.GetRandomPort());
        var dispatcherHttpPort = TestPortAllocator.GetRandomPort();
        var workerNode = new GossipNodeDescriptor("mesh-node-worker", "rack-worker", TestPortAllocator.GetRandomPort());
        var gatewayNode = new GossipNodeDescriptor("mesh-node-gateway", "rack-gateway", TestPortAllocator.GetRandomPort());

        var workerHost = CreateHost(workerNode, [$"{LoopbackAddress}:{dispatcherNode.Port}", $"{LoopbackAddress}:{gatewayNode.Port}"]);
        var gatewayHost = CreateHost(gatewayNode, [$"{LoopbackAddress}:{dispatcherNode.Port}", $"{LoopbackAddress}:{workerNode.Port}"]);

        await StartHostsAsync(ct, workerHost, gatewayHost);

        IHost? dispatcherHost = null;
        try
        {
            var dispatcherSeeds = new[] { $"{LoopbackAddress}:{workerNode.Port}", $"{LoopbackAddress}:{gatewayNode.Port}" };
            dispatcherHost = await StartDispatcherHostAsync(dispatcherNode, dispatcherHttpPort, dispatcherSeeds, ct);
            var dispatcherAgent = dispatcherHost.Services.GetRequiredService<IMeshGossipAgent>();

            await WaitForConditionAsync(
                "three-node gossip convergence",
                () =>
                    AllAlive(workerHost, dispatcherNode.NodeId, gatewayNode.NodeId) &&
                    AllAlive(gatewayHost, dispatcherNode.NodeId, workerNode.NodeId) &&
                    AllAlive(dispatcherAgent, workerNode.NodeId, gatewayNode.NodeId),
                () => DescribeSnapshots(workerHost, gatewayHost, dispatcherAgent),
                ConvergenceTimeout,
                ct);

            using var httpClient = new HttpClient { BaseAddress = new Uri($"http://{LoopbackAddress}:{dispatcherHttpPort}/", UriKind.Absolute) };
            using var diagnostics = await ReadPeerDiagnosticsWithRetryAsync(httpClient, ct);

            var root = diagnostics.RootElement;
            Assert.Equal(dispatcherAgent.LocalMetadata.NodeId, root.GetProperty("localNodeId").GetString());

            var peers = root.GetProperty("peers")
                .EnumerateArray()
                .ToDictionary(peer => peer.GetProperty("nodeId").GetString()!, peer => peer);

            Assert.Equal(3, peers.Count);
            AssertPeerDiagnosticsEntry(peers[dispatcherNode.NodeId], dispatcherNode);
            AssertPeerDiagnosticsEntry(peers[workerNode.NodeId], workerNode);
            AssertPeerDiagnosticsEntry(peers[gatewayNode.NodeId], gatewayNode);
        }
        finally
        {
            await StopAndDisposeAsync(workerHost, gatewayHost);

            if (dispatcherHost is not null)
            {
                await dispatcherHost.StopAsync(CancellationToken.None);
                dispatcherHost.Dispose();
            }
        }
    }

    private MeshGossipHost CreateHost(GossipNodeDescriptor descriptor, IReadOnlyList<string> seedPeers)
    {
        var options = new MeshGossipOptions
        {
            NodeId = descriptor.NodeId,
            Role = "dispatcher",
            ClusterId = "integration-cluster",
            Region = "integration-region",
            MeshVersion = "integration-test",
            BindAddress = LoopbackAddress,
            AdvertiseHost = LoopbackAddress,
            AdvertisePort = descriptor.Port,
            Port = descriptor.Port,
            Interval = TimeSpan.FromMilliseconds(200),
            SuspicionInterval = TimeSpan.FromSeconds(2),
            PingTimeout = TimeSpan.FromMilliseconds(500),
            RetransmitLimit = 2,
            Fanout = 1,
            MetadataRefreshPeriod = TimeSpan.FromSeconds(2),
            CertificateReloadInterval = TimeSpan.FromSeconds(30),
            Http3Support = true,
            SeedPeers = seedPeers.ToList(),
            Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["rack"] = descriptor.Rack
            },
            Tls = new MeshGossipTlsOptions
            {
                CertificateData = _certificate.CertificateData,
                CertificatePassword = _certificate.Password,
                AllowUntrustedCertificates = true,
                CheckCertificateRevocation = false
            }
        };

        var logger = LoggerFactory.CreateLogger<MeshGossipHost>();
        return new MeshGossipHost(options, metadata: null, logger, LoggerFactory);
    }

    private async Task<IHost> StartDispatcherHostAsync(
        GossipNodeDescriptor descriptor,
        int httpPort,
        IReadOnlyList<string> seedPeers,
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
        builder.Services.AddOmniRelayDispatcher(builder.Configuration.GetSection("omnirelay"));

        var host = builder.Build();
        await host.StartAsync(cancellationToken);
        return host;
    }

    private static async Task StartHostsAsync(CancellationToken cancellationToken, params MeshGossipHost[] hosts)
    {
        foreach (var host in hosts)
        {
            await host.StartAsync(cancellationToken);
        }
    }

    private static async Task StopAndDisposeAsync(params MeshGossipHost[] hosts)
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
            finally
            {
                host.Dispose();
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
        nodeIds.All(nodeId => HasStatus(agent, nodeId, MeshGossipMemberStatus.Alive));

    private static async Task WaitForConditionAsync(
        string description,
        Func<bool> predicate,
        Func<string> diagnostics,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
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
        Assert.Equal("Alive", peer.GetProperty("status").GetString());

        var metadata = peer.GetProperty("metadata");
        Assert.Equal("dispatcher", metadata.GetProperty("role").GetString());
        Assert.Equal("integration-cluster", metadata.GetProperty("clusterId").GetString());
        Assert.Equal("integration-region", metadata.GetProperty("region").GetString());
        Assert.Equal("integration-test", metadata.GetProperty("meshVersion").GetString());
        Assert.True(metadata.GetProperty("http3Support").GetBoolean());
        Assert.Equal($"127.0.0.1:{descriptor.Port}", metadata.GetProperty("endpoint").GetString());
        Assert.Equal(descriptor.Rack, metadata.GetProperty("labels").GetProperty("rack").GetString());

        var lastSeen = peer.GetProperty("lastSeen").GetString();
        Assert.False(string.IsNullOrWhiteSpace(lastSeen));

        if (peer.TryGetProperty("rttMs", out var rtt) && rtt.ValueKind == JsonValueKind.Number)
        {
            Assert.True(rtt.GetDouble() >= 0);
        }
    }

    private static bool ShouldRetry(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException or JsonException;

    private sealed record GossipNodeDescriptor(string NodeId, string Rack, int Port);
}
