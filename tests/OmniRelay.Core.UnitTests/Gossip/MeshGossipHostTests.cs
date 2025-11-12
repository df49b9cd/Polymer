using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.Core.Gossip;
using OmniRelay.Core.Peers;
using OmniRelay.Tests.Support;
using Xunit;

namespace OmniRelay.Core.UnitTests.Gossip;

public sealed class MeshGossipHostTests
{
    private static readonly MethodInfo ProcessEnvelopeAsyncMethod =
        typeof(MeshGossipHost).GetMethod("ProcessEnvelopeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo RecordMetricsMethod =
        typeof(MeshGossipHost).GetMethod("RecordMetrics", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo BuildEnvelopeMethod =
        typeof(MeshGossipHost).GetMethod("BuildEnvelope", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MembershipField =
        typeof(MeshGossipHost).GetField("_membership", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public void Constructor_ThrowsWhenFanoutInvalid()
    {
        var options = CreateOptions();
        options.Fanout = 0;

        Should.Throw<ArgumentOutOfRangeException>(() =>
            new MeshGossipHost(options, metadata: null, NullLogger<MeshGossipHost>.Instance, NullLoggerFactory.Instance));
    }

    [Fact]
    public async Task ProcessEnvelopeAsync_ReturnsLocalEnvelopeWhenSchemaMismatch()
    {
        var (host, _) = CreateHost();

        try
        {
            var envelope = new MeshGossipEnvelope { SchemaVersion = "legacy" };

            var response = await InvokeProcessEnvelopeAsync(host, envelope, TestContext.Current.CancellationToken);

            response.SchemaVersion.ShouldBe(MeshGossipOptions.CurrentSchemaVersion);
            response.Sender.NodeId.ShouldBe(host.LocalMetadata.NodeId);
            response.Members.ShouldContain(m => m.NodeId == host.LocalMetadata.NodeId);
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact]
    public async Task ProcessEnvelopeAsync_MergesSenderAndMembersIntoSnapshot()
    {
        var (host, _) = CreateHost();

        try
        {
            var senderMetadata = new MeshGossipMemberMetadata
            {
                NodeId = "peer-sender",
                Role = "dispatcher",
                ClusterId = "cluster-a",
                Region = "region-1",
                MeshVersion = "1.0.0",
                MetadataVersion = 3,
                Http3Support = true,
                Labels = ImmutableDictionary<string, string>.Empty
            };

            var otherMetadata = new MeshGossipMemberMetadata
            {
                NodeId = "peer-member",
                Role = "worker",
                ClusterId = "cluster-b",
                Region = "region-2",
                MeshVersion = "2.0.0",
                MetadataVersion = 2,
                Http3Support = false,
                Labels = ImmutableDictionary<string, string>.Empty
            };

            var envelope = new MeshGossipEnvelope
            {
                SchemaVersion = MeshGossipOptions.CurrentSchemaVersion,
                Sender = senderMetadata,
                Members = new[]
                {
                    new MeshGossipMemberSnapshot
                    {
                        NodeId = otherMetadata.NodeId,
                        Status = MeshGossipMemberStatus.Suspect,
                        LastSeen = DateTimeOffset.UtcNow,
                        Metadata = otherMetadata
                    }
                }
            };

            await InvokeProcessEnvelopeAsync(host, envelope, TestContext.Current.CancellationToken);

            var snapshot = host.Snapshot();
            snapshot.Members.ShouldContain(m => m.NodeId == senderMetadata.NodeId && m.Metadata.Role == "dispatcher");
            snapshot.Members.ShouldContain(m => m.NodeId == otherMetadata.NodeId && m.Status == MeshGossipMemberStatus.Suspect);
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact]
    public void BuildEnvelope_TruncatesMembershipToThirtyTwoEntries()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (host, _) = CreateHost(timeProvider: time);

        try
        {
            var membership = GetMembership(host);

            for (var i = 0; i < 50; i++)
            {
                var metadata = new MeshGossipMemberMetadata
                {
                    NodeId = $"peer-{i:D2}",
                    Role = "worker",
                    ClusterId = "cluster",
                    Region = "region",
                    MeshVersion = $"1.0.{i}",
                    MetadataVersion = i + 1,
                    Labels = ImmutableDictionary<string, string>.Empty
                };

                membership.MarkObserved(new MeshGossipMemberSnapshot
                {
                    NodeId = metadata.NodeId,
                    Status = MeshGossipMemberStatus.Alive,
                    LastSeen = time.GetUtcNow(),
                    Metadata = metadata
                });
            }

            var envelope = InvokeBuildEnvelope(host);
            var nextEnvelope = InvokeBuildEnvelope(host);

            envelope.Members.Count.ShouldBe(32);
            envelope.Sender.NodeId.ShouldBe(host.LocalMetadata.NodeId);
            (nextEnvelope.Sequence > envelope.Sequence).ShouldBeTrue();
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact]
    public void RecordMetrics_LogsPeerLifecycleAndDisconnects()
    {
        var sharedTime = new TestTimeProvider(DateTimeOffset.UtcNow);
        var tracker = new PeerLeaseHealthTracker(timeProvider: sharedTime);
        var (host, logger) = CreateHost(timeProvider: sharedTime, tracker: tracker);

        try
        {
            var remoteMetadata = new MeshGossipMemberMetadata
            {
                NodeId = "peer-observed",
                Role = "worker",
                ClusterId = "cluster-a",
                Region = "region-a",
                MeshVersion = "1.2.3",
                MetadataVersion = 1,
                Http3Support = true,
                Labels = ImmutableDictionary<string, string>.Empty
            };

            var statuses = new[]
            {
                MeshGossipMemberStatus.Alive,
                MeshGossipMemberStatus.Suspect,
                MeshGossipMemberStatus.Alive,
                MeshGossipMemberStatus.Left
            };

            foreach (var status in statuses)
            {
                var snapshot = CreateClusterSnapshot(host.LocalMetadata, remoteMetadata, status, sharedTime.GetUtcNow());
                InvokeRecordMetrics(host, snapshot);
                sharedTime.Advance(TimeSpan.FromSeconds(1));
            }

            logger.Entries.ShouldContain(entry => entry.Message.Contains("joined cluster") && entry.Message.Contains("peer-observed"));
            logger.Entries.ShouldContain(entry => entry.Message.Contains("marked suspect"));
            logger.Entries.ShouldContain(entry => entry.Message.Contains("recovered from"));
            logger.Entries.ShouldContain(entry => entry.Message.Contains("left gossip cluster"));

            var peerSnapshot = tracker.Snapshot().First(s => s.PeerId == remoteMetadata.NodeId);
            peerSnapshot.Metadata.TryGetValue("disconnect.reason", out var reason).ShouldBeTrue();
            reason.ShouldBe("gossip-left");
        }
        finally
        {
            host.Dispose();
        }
    }

    private static MeshGossipClusterView CreateClusterSnapshot(
        MeshGossipMemberMetadata localMetadata,
        MeshGossipMemberMetadata remoteMetadata,
        MeshGossipMemberStatus status,
        DateTimeOffset observedAt)
    {
        var localSnapshot = new MeshGossipMemberSnapshot
        {
            NodeId = localMetadata.NodeId,
            Status = MeshGossipMemberStatus.Alive,
            Metadata = localMetadata
        };

        var remoteSnapshot = new MeshGossipMemberSnapshot
        {
            NodeId = remoteMetadata.NodeId,
            Status = status,
            LastSeen = observedAt,
            Metadata = remoteMetadata
        };

        var members = ImmutableArray.Create(localSnapshot, remoteSnapshot);
        return new MeshGossipClusterView(observedAt, members, localMetadata.NodeId, MeshGossipOptions.CurrentSchemaVersion);
    }

    private static MeshGossipMembershipTable GetMembership(MeshGossipHost host) =>
        (MeshGossipMembershipTable)MembershipField.GetValue(host)!;

    private static MeshGossipOptions CreateOptions()
    {
        var options = new MeshGossipOptions
        {
            Enabled = true,
            NodeId = "local-node",
            Role = "dispatcher",
            ClusterId = "cluster-a",
            Region = "region-0",
            MeshVersion = "dev",
            BindAddress = "127.0.0.1",
            Port = 19100,
            Fanout = 3,
            Interval = TimeSpan.FromMilliseconds(10),
            SuspicionInterval = TimeSpan.FromMilliseconds(10),
            PingTimeout = TimeSpan.FromMilliseconds(5),
            MetadataRefreshPeriod = TimeSpan.FromSeconds(30)
        };
        options.Tls.CertificatePath = "test-cert.pfx";
        options.Tls.CheckCertificateRevocation = false;
        return options;
    }

    private static (MeshGossipHost Host, TestLogger<MeshGossipHost> Logger) CreateHost(
        MeshGossipOptions? options = null,
        TimeProvider? timeProvider = null,
        PeerLeaseHealthTracker? tracker = null)
    {
        options ??= CreateOptions();
        var logger = new TestLogger<MeshGossipHost>();
        var host = new MeshGossipHost(
            options,
            metadata: null,
            logger,
            NullLoggerFactory.Instance,
            timeProvider ?? new TestTimeProvider(DateTimeOffset.UtcNow),
            tracker,
            certificateProvider: null);
        return (host, logger);
    }

    private static MeshGossipEnvelope InvokeBuildEnvelope(MeshGossipHost host, MeshGossipClusterView? snapshot = null) =>
        (MeshGossipEnvelope)BuildEnvelopeMethod.Invoke(host, new object?[] { snapshot })!;

    private static void InvokeRecordMetrics(MeshGossipHost host, MeshGossipClusterView snapshot) =>
        RecordMetricsMethod.Invoke(host, new object[] { snapshot });

    private static Task<MeshGossipEnvelope> InvokeProcessEnvelopeAsync(
        MeshGossipHost host,
        MeshGossipEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var task = (Task<MeshGossipEnvelope>)ProcessEnvelopeAsyncMethod.Invoke(host, new object?[] { envelope, cancellationToken })!;
        return task;
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _current;

        public TestTimeProvider(DateTimeOffset start) => _current = start;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan delta) => _current += delta;
    }
}
