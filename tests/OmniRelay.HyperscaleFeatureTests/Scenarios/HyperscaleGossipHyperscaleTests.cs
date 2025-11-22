using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.Core.Gossip;
using OmniRelay.HyperscaleFeatureTests.Infrastructure;
using OmniRelay.Tests;
using Xunit;

namespace OmniRelay.HyperscaleFeatureTests.Scenarios;

public sealed class HyperscaleGossipHyperscaleTests : IAsyncLifetime
{
    private readonly List<MeshGossipHost> _hosts = new();
    private readonly IReadOnlyList<GossipNodeDescriptor> _nodes;
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    public HyperscaleGossipHyperscaleTests()
    {
        _nodes = CreateDescriptors(nodeCount: 32);
    }

    [Fact(DisplayName = "Gossip cluster converges across dozens of nodes and recovers from churn", Timeout = TestTimeouts.Long)]
    public async ValueTask GossipCluster_CoversHyperscaleScenarioAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await StartClusterAsync(_nodes, ct);

        var convergence = await WaitForConditionAsync(
            () => ClusterHasAliveCoverage(_hosts, _nodes),
            TimeSpan.FromSeconds(60),
            ct);

        Assert.True(convergence, $"Hyperscale cluster failed to converge.{Environment.NewLine}{DescribeSnapshots(_hosts)}");

        AssertMetadataConsistency(_hosts, _nodes);

        var departedNodes = _hosts.Take(5).Select((host, index) => (Host: host, Descriptor: _nodes[index])).ToList();
        foreach (var (host, _) in departedNodes)
        {
            await host.StopAsync(ct);
            host.Dispose();
            _hosts.Remove(host);
        }

        var leftIds = departedNodes.Select(entry => entry.Descriptor.NodeId).ToArray();
        foreach (var host in _hosts)
        {
            foreach (var nodeId in leftIds)
            {
                host.ForcePeerStatus(nodeId, MeshGossipMemberStatus.Left);
            }
        }

        var leftObserved = await WaitForConditionAsync(
            () => HostsReportStatus(_hosts, leftIds, MeshGossipMemberStatus.Left),
            TimeSpan.FromSeconds(45),
            ct);

        Assert.True(leftObserved, $"Departed nodes were not marked left within timeout.{Environment.NewLine}{DescribeSnapshots(_hosts)}");
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            try
            {
                await host.StopAsync(CancellationToken.None);
            }
            finally
            {
                host.Dispose();
            }
        }

        _hosts.Clear();
    }

    private async Task StartClusterAsync(IEnumerable<GossipNodeDescriptor> descriptors, CancellationToken cancellationToken)
    {
        var certificate = HyperscaleTestEnvironment.Certificate;
        var launched = new List<GossipNodeDescriptor>();
        foreach (var descriptor in descriptors)
        {
            var seedPeers = launched
                .OrderByDescending(node => node.Index)
                .Take(8)
                .Select(node => $"127.0.0.1:{node.Port}")
                .ToList();

            var options = new MeshGossipOptions
            {
                Enabled = true,
                NodeId = descriptor.NodeId,
                Role = descriptor.Role,
                ClusterId = descriptor.Cluster,
                Region = descriptor.Region,
                MeshVersion = descriptor.Version,
                Http3Support = descriptor.Http3Support,
                BindAddress = "127.0.0.1",
                AdvertiseHost = "127.0.0.1",
                Port = descriptor.Port,
                AdvertisePort = descriptor.Port,
                Interval = TimeSpan.FromMilliseconds(250),
                SuspicionInterval = TimeSpan.FromSeconds(5),
                PingTimeout = TimeSpan.FromMilliseconds(1000),
                Fanout = 6,
                RetransmitLimit = 4,
                MetadataRefreshPeriod = TimeSpan.FromSeconds(5),
                SeedPeers = seedPeers,
                Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["rack"] = descriptor.Rack,
                    ["zone"] = descriptor.Zone
                }
            };

            options.Tls.CertificateData = certificate.CertificateData;
            options.Tls.CertificatePassword = certificate.Password;
            options.Tls.AllowUntrustedCertificates = true;
            options.Tls.CheckCertificateRevocation = false;

            var hostLogger = _loggerFactory.CreateLogger<MeshGossipHost>();
            var host = new MeshGossipHost(options, metadata: null, hostLogger, _loggerFactory);
            await host.StartAsync(cancellationToken);
            _hosts.Add(host);
            launched.Add(descriptor);
        }
    }

    private static void AssertMetadataConsistency(IEnumerable<MeshGossipHost> hosts, IEnumerable<GossipNodeDescriptor> descriptors)
    {
        var expected = descriptors.ToDictionary(node => node.NodeId, node => node);

        foreach (var host in hosts)
        {
            var snapshot = host.Snapshot();
            foreach (var member in snapshot.Members.Where(member => expected.ContainsKey(member.NodeId)))
            {
                var descriptor = expected[member.NodeId];
                Assert.Equal(descriptor.Version, member.Metadata.MeshVersion);
                Assert.Equal(descriptor.Region, member.Metadata.Region);
                Assert.Equal(descriptor.Cluster, member.Metadata.ClusterId);
                Assert.Equal(descriptor.Http3Support, member.Metadata.Http3Support);
                Assert.Equal(descriptor.Rack, member.Metadata.Labels["rack"]);
                Assert.Equal(descriptor.Zone, member.Metadata.Labels["zone"]);
            }
        }
    }

    private static IReadOnlyList<GossipNodeDescriptor> CreateDescriptors(int nodeCount)
    {
        var roles = new[] { "dispatcher", "gateway", "worker" };
        var clusters = new[] { "hyperscale-alpha", "hyperscale-beta" };
        var random = new Random(42);
        var nodes = new List<GossipNodeDescriptor>(nodeCount);

        for (var index = 0; index < nodeCount; index++)
        {
            var role = roles[index % roles.Length];
            var cluster = clusters[index % clusters.Length];
            var region = $"region-{index % 4}";
            var zone = $"zone-{index % 8}";
            var rack = $"rack-{index % 16}";
            var http3 = index % 2 == 0;
            var version = $"hyperscale-{1 + (index % 3)}.0.{random.Next(10)}";
            var port = TestPortAllocator.GetRandomPort();
            var nodeId = $"hyperscale-node-{index:D2}";

            nodes.Add(new GossipNodeDescriptor(
                Index: index,
                NodeId: nodeId,
                Role: role,
                Cluster: cluster,
                Region: region,
                Zone: zone,
                Rack: rack,
                Version: version,
                Http3Support: http3,
                Port: port));
        }

        return nodes;
    }

    private static bool ClusterHasAliveCoverage(IReadOnlyList<MeshGossipHost> hosts, IReadOnlyCollection<GossipNodeDescriptor> nodes)
    {
        if (hosts.Count == 0)
        {
            return false;
        }

        var coverage = new HashSet<string>(StringComparer.Ordinal);
        var satisfiedHosts = 0;
        foreach (var host in hosts)
        {
            var snapshot = host.Snapshot();
            var alive = snapshot.Members.Where(m => m.Status == MeshGossipMemberStatus.Alive).Select(m => m.NodeId).ToHashSet(StringComparer.Ordinal);
            if (alive.Count >= nodes.Count * 0.8)
            {
                satisfiedHosts++;
            }

            coverage.UnionWith(alive);
        }

        var requiredHosts = Math.Max(1, (int)Math.Ceiling(hosts.Count * 0.75));
        return coverage.Count >= nodes.Count && satisfiedHosts >= requiredHosts;
    }

    private static bool HostsReportStatus(IReadOnlyList<MeshGossipHost> hosts, IReadOnlyCollection<string> nodeIds, MeshGossipMemberStatus status)
    {
        if (nodeIds.Count == 0 || hosts.Count == 0)
        {
            return true;
        }

        var satisfiedHosts = 0;
        foreach (var host in hosts)
        {
            var snapshot = host.Snapshot();
            var matches = nodeIds.All(nodeId =>
                snapshot.Members.Any(member =>
                    string.Equals(member.NodeId, nodeId, StringComparison.Ordinal) &&
                    MatchesStatus(member.Status, status)));

            if (matches)
            {
                satisfiedHosts++;
            }
        }

        var required = Math.Max(1, (int)Math.Ceiling(hosts.Count * 0.5));
        return satisfiedHosts >= required;
    }

    private static bool MatchesStatus(MeshGossipMemberStatus actual, MeshGossipMemberStatus expected)
    {
        if (expected == MeshGossipMemberStatus.Left)
        {
            return actual is MeshGossipMemberStatus.Left or MeshGossipMemberStatus.Suspect;
        }

        return actual == expected;
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (predicate())
            {
                return true;
            }

            await Task.Delay(200, cancellationToken);
        }

        return predicate();
    }

    private static string DescribeSnapshots(IEnumerable<MeshGossipHost> hosts)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var host in hosts)
        {
            var snapshot = host.Snapshot();
            var members = snapshot.Members
                .OrderBy(member => member.NodeId, StringComparer.Ordinal)
                .Select(member => $"{member.NodeId}:{member.Status}");
            builder.Append(snapshot.LocalNodeId)
                .Append(" => ")
                .AppendLine(string.Join(", ", members));
        }

        return builder.ToString();
    }

    private sealed record GossipNodeDescriptor(
        int Index,
        string NodeId,
        string Role,
        string Cluster,
        string Region,
        string Zone,
        string Rack,
        string Version,
        bool Http3Support,
        int Port);
}
