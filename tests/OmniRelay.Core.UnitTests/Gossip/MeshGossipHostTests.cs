using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.Core.Gossip;
using OmniRelay.Diagnostics;
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
    private static readonly MethodInfo ExecuteRoundAsyncMethod =
        typeof(MeshGossipHost).GetMethod("ExecuteRoundAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo RunSweepLoopAsyncMethod =
        typeof(MeshGossipHost).GetMethod("RunSweepLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo UpdateLeaseDiagnosticsMethod =
        typeof(MeshGossipHost).GetMethod("UpdateLeaseDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo MembershipField =
        typeof(MeshGossipHost).GetField("_membership", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo HttpClientField =
        typeof(MeshGossipHost).GetField("_httpClient", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact(Timeout = TestTimeouts.Default)]
    public void Constructor_ThrowsWhenFanoutInvalid()
    {
        var options = CreateOptions();
        options.Fanout = 0;

        Should.Throw<ArgumentOutOfRangeException>(() =>
            new MeshGossipHost(options, metadata: null, NullLogger<MeshGossipHost>.Instance, NullLoggerFactory.Instance));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ProcessEnvelopeAsync_ReturnsLocalEnvelopeWhenSchemaMismatch()
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

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ProcessEnvelopeAsync_MergesSenderAndMembersIntoSnapshot()
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
                Members =
                [
                    new MeshGossipMemberSnapshot
                    {
                        NodeId = otherMetadata.NodeId,
                        Status = MeshGossipMemberStatus.Suspect,
                        LastSeen = DateTimeOffset.UtcNow,
                        Metadata = otherMetadata
                    }
                ]
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

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ExecuteRoundAsync_GossipsWithKnownPeersAndUpdatesMembership()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var (host, _) = CreateHost(timeProvider: time);
        var membership = GetMembership(host);

        var remoteMetadata = new MeshGossipMemberMetadata
        {
            NodeId = "peer-target",
            Role = "worker",
            ClusterId = "cluster-a",
            Region = "region-a",
            MeshVersion = "1.0.0",
            MetadataVersion = 1,
            Http3Support = true,
            Endpoint = "gossip-peer:19001"
        };

        membership.MarkObserved(new MeshGossipMemberSnapshot
        {
            NodeId = remoteMetadata.NodeId,
            Status = MeshGossipMemberStatus.Alive,
            LastSeen = time.GetUtcNow(),
            Metadata = remoteMetadata
        });

        MeshGossipEnvelope? observedRequest = null;
        var handler = new TestHttpMessageHandler(async (request, cancellationToken) =>
        {
            observedRequest = await request.Content!
                .ReadFromJsonAsync(MeshGossipJsonSerializerContext.Default.MeshGossipEnvelope, cancellationToken);

            var updatedRemote = remoteMetadata with { MetadataVersion = 2, MeshVersion = "1.1.0" };
            var response = new MeshGossipEnvelope
            {
                Sender = updatedRemote,
                Members =
                [
                    new MeshGossipMemberSnapshot
                    {
                        NodeId = updatedRemote.NodeId,
                        Status = MeshGossipMemberStatus.Alive,
                        LastSeen = time.GetUtcNow(),
                        Metadata = updatedRemote
                    },
                    new MeshGossipMemberSnapshot
                    {
                        NodeId = "peer-observed",
                        Status = MeshGossipMemberStatus.Alive,
                        LastSeen = time.GetUtcNow(),
                        Metadata = new MeshGossipMemberMetadata
                        {
                            NodeId = "peer-observed",
                            Role = "gateway",
                            ClusterId = "cluster-a",
                            Region = "region-b",
                            MeshVersion = "2.0.0",
                            MetadataVersion = 1,
                            Labels = ImmutableDictionary<string, string>.Empty
                        }
                    }
                ]
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(response, MeshGossipJsonSerializerContext.Default.MeshGossipEnvelope)
            };
        });

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(1)
        };
        SetHttpClient(host, client);

        try
        {
            await InvokeExecuteRoundAsync(host, CancellationToken.None);

            observedRequest.ShouldNotBeNull();
            observedRequest!.Members.ShouldContain(m => m.NodeId == host.LocalMetadata.NodeId);

            var snapshot = host.Snapshot();
            snapshot.Members.ShouldContain(m => m.NodeId == "peer-observed" && m.Metadata.Role == "gateway");
            snapshot.Members.ShouldContain(m => m.NodeId == remoteMetadata.NodeId && m.Metadata.MeshVersion == "1.1.0");
        }
        finally
        {
            client.Dispose();
            host.Dispose();
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask RunSweepLoopAsync_TransitionsPeersBasedOnTimers()
    {
        var start = DateTimeOffset.UtcNow;
        var time = new TestTimeProvider(start);
        var options = CreateOptions();
        options.SuspicionInterval = TimeSpan.FromMilliseconds(1);
        options.PingTimeout = TimeSpan.FromMilliseconds(1);
        options.RetransmitLimit = 1;

        var (host, _) = CreateHost(options, time);
        var membership = GetMembership(host);

        membership.MarkObserved(new MeshGossipMemberSnapshot
        {
            NodeId = "peer-suspect",
            Status = MeshGossipMemberStatus.Alive,
            LastSeen = start - TimeSpan.FromMilliseconds(1.5),
            Metadata = new MeshGossipMemberMetadata
            {
                NodeId = "peer-suspect",
                Role = "worker",
                ClusterId = "cluster-a",
                Region = "region-a",
                MeshVersion = "1.0.0",
                MetadataVersion = 1
            }
        });

        membership.MarkObserved(new MeshGossipMemberSnapshot
        {
            NodeId = "peer-left",
            Status = MeshGossipMemberStatus.Alive,
            LastSeen = start - TimeSpan.FromMilliseconds(3),
            Metadata = new MeshGossipMemberMetadata
            {
                NodeId = "peer-left",
                Role = "worker",
                ClusterId = "cluster-a",
                Region = "region-a",
                MeshVersion = "1.0.0",
                MetadataVersion = 1
            }
        });

        try
        {
            membership.Sweep(
                options.SuspicionInterval,
                options.SuspicionInterval + options.PingTimeout * Math.Max(1, options.RetransmitLimit));

            var snapshot = host.Snapshot();
            snapshot.Members.ShouldContain(m => m.NodeId == "peer-suspect" && m.Status == MeshGossipMemberStatus.Suspect);
            snapshot.Members.ShouldContain(m => m.NodeId == "peer-left" && m.Status == MeshGossipMemberStatus.Left);
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void UpdateLeaseDiagnostics_PopulatesTrackerMetadata()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var tracker = new PeerLeaseHealthTracker(timeProvider: time);
        var (host, _) = CreateHost(timeProvider: time, tracker: tracker);

        try
        {
            var membership = GetMembership(host);
            var remoteMetadata = new MeshGossipMemberMetadata
            {
                NodeId = "peer-diagnostics",
                Role = "gateway",
                ClusterId = "cluster-a",
                Region = "region-b",
                MeshVersion = "1.2.3",
                MetadataVersion = 1,
                Http3Support = false,
                Labels = ImmutableDictionary<string, string>.Empty.Add("mesh.zone", "az-1")
            };

            membership.MarkObserved(new MeshGossipMemberSnapshot
            {
                NodeId = remoteMetadata.NodeId,
                Status = MeshGossipMemberStatus.Alive,
                LastSeen = time.GetUtcNow(),
                Metadata = remoteMetadata
            });

            InvokeUpdateLeaseDiagnostics(host);

            var peer = tracker.Snapshot().FirstOrDefault(s => s.PeerId == "peer-diagnostics");
            peer.ShouldNotBeNull();
            peer!.Metadata["mesh.role"].ShouldBe("gateway");
            peer.Metadata["mesh.cluster"].ShouldBe("cluster-a");
            peer.Metadata["mesh.region"].ShouldBe("region-b");
            peer.Metadata["mesh.version"].ShouldBe("1.2.3");
            peer.Metadata["mesh.http3"].ShouldBe("false");
            peer.Metadata["label.mesh.zone"].ShouldBe("az-1");
        }
        finally
        {
            host.Dispose();
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
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
            tlsManager: null);
        return (host, logger);
    }

    private static MeshGossipEnvelope InvokeBuildEnvelope(MeshGossipHost host, MeshGossipClusterView? snapshot = null) =>
        (MeshGossipEnvelope)BuildEnvelopeMethod.Invoke(host, [snapshot])!;

    private static void InvokeRecordMetrics(MeshGossipHost host, MeshGossipClusterView snapshot) =>
        RecordMetricsMethod.Invoke(host, [snapshot]);

    private static Task InvokeExecuteRoundAsync(MeshGossipHost host, CancellationToken cancellationToken) =>
        (Task)ExecuteRoundAsyncMethod.Invoke(host, [cancellationToken])!;

    private static Task InvokeRunSweepLoopAsync(MeshGossipHost host, CancellationToken cancellationToken) =>
        (Task)RunSweepLoopAsyncMethod.Invoke(host, [cancellationToken])!;

    private static void InvokeUpdateLeaseDiagnostics(MeshGossipHost host) =>
        UpdateLeaseDiagnosticsMethod.Invoke(host, Array.Empty<object?>());

    private static void SetHttpClient(MeshGossipHost host, HttpClient client) =>
        HttpClientField.SetValue(host, client);

    private static Task<MeshGossipEnvelope> InvokeProcessEnvelopeAsync(
        MeshGossipHost host,
        MeshGossipEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var task = (Task<MeshGossipEnvelope>)ProcessEnvelopeAsyncMethod.Invoke(host, [envelope, cancellationToken])!;
        return task;
    }

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public TestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }

    private sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _current = start;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan delta) => _current += delta;
    }
}
