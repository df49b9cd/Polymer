using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OmniRelay.Core.Gossip;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests;
using OmniRelay.Tests.Support;
using Xunit;

namespace OmniRelay.IntegrationTests;

public sealed class GossipIntegrationTests : IntegrationTest
{
    private const string LoopbackAddress = "127.0.0.1";
    private static readonly TimeSpan ConvergenceTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DepartureTimeout = TimeSpan.FromSeconds(20);
    private readonly TestCertificateInfo _certificate;

    public GossipIntegrationTests(ITestOutputHelper output)
        : base(output)
    {
        _certificate = TestCertificateFactory.EnsureDeveloperCertificateInfo("CN=integration-gossip");
    }

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

    private static MeshGossipMemberSnapshot? GetMember(MeshGossipHost host, string nodeId) =>
        host.Snapshot().Members.FirstOrDefault(member => string.Equals(member.NodeId, nodeId, StringComparison.Ordinal));

    private static bool IsAlive(MeshGossipHost host, string nodeId) =>
        HasStatus(host, nodeId, MeshGossipMemberStatus.Alive);

    private static bool HasStatus(MeshGossipHost host, string nodeId, MeshGossipMemberStatus status) =>
        host.Snapshot().Members.Any(member =>
            string.Equals(member.NodeId, nodeId, StringComparison.Ordinal) && member.Status == status);

    private async Task WaitForConditionAsync(
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

    private static string DescribeSnapshots(params MeshGossipHost[] hosts)
    {
        var builder = new StringBuilder();
        foreach (var host in hosts)
        {
            if (host is null)
            {
                continue;
            }

            var snapshot = host.Snapshot();
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

    private sealed record GossipNodeDescriptor(string NodeId, string Rack, int Port);
}
