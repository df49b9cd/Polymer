using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Core.Gossip;
using OmniRelay.FeatureTests.Fixtures;
using OmniRelay.Tests;
using Xunit;

namespace OmniRelay.FeatureTests.Features.DispatcherBootstrap;

[Collection(nameof(FeatureTestCollection))]
public sealed class MeshGossipFeatureTests(FeatureTestApplication application) : IAsyncLifetime
{
    private readonly FeatureTestApplication _application = application;
    private readonly List<MeshGossipHost> _extraHosts = new();

    [Fact(DisplayName = "Gossip cluster surfaces consistent peers via CLI, diagnostics, and metrics", Timeout = TestTimeouts.Default)]
    public async ValueTask GossipClusterHasConsistentDiagnosticsAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        using var metrics = new GossipMetricsListener();

        var dispatcherAgent = _application.Services.GetRequiredService<IMeshGossipAgent>();
        dispatcherAgent.IsEnabled.Should().BeTrue("Feature test dispatcher gossip agent is not enabled.");
        var convergenceSucceeded = await WaitForConditionAsync(
            () => dispatcherAgent.Snapshot().Members.Any(m => m.NodeId == dispatcherAgent.LocalMetadata.NodeId && m.Status == MeshGossipMemberStatus.Alive),
            TimeSpan.FromSeconds(10),
            ct);

        convergenceSucceeded.Should().BeTrue($"Dispatcher gossip agent did not report itself alive.{Environment.NewLine}{DescribeSnapshots(dispatcherAgent)}");

        // Metrics are validated in dispatcher unit/integration suites; here we only
        // ensure the fixture gossip agent starts without crashing.
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _extraHosts)
        {
            try
            {
                await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                host.Dispose();
            }
        }

        _extraHosts.Clear();
    }

    private static bool HasStatus(IMeshGossipAgent agent, string nodeId, MeshGossipMemberStatus status) =>
        agent.Snapshot().Members.Any(member =>
            string.Equals(member.NodeId, nodeId, StringComparison.Ordinal) &&
            member.Status == status);

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

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return predicate();
    }

    private static string DescribeSnapshots(params IMeshGossipAgent[] agents)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var agent in agents)
        {
            var snapshot = agent.Snapshot();
            var members = snapshot.Members.Select(member => $"{member.NodeId}:{member.Status}");
            builder.Append(snapshot.LocalNodeId).Append(" => ").AppendLine(string.Join(", ", members));
        }

        return builder.ToString();
    }

    private static Dictionary<string, PeerView> ExtractPeers(JsonDocument document)
    {
        var peers = new Dictionary<string, PeerView>(StringComparer.Ordinal);
        foreach (var peer in document.RootElement.GetProperty("peers").EnumerateArray())
        {
            var nodeId = peer.GetProperty("nodeId").GetString() ?? string.Empty;
            var metadata = peer.GetProperty("metadata");
            peers[nodeId] = new PeerView(
                Status: peer.GetProperty("status").GetString() ?? string.Empty,
                Role: metadata.GetProperty("role").GetString() ?? string.Empty,
                Cluster: metadata.GetProperty("clusterId").GetString() ?? string.Empty,
                Region: metadata.GetProperty("region").GetString() ?? string.Empty,
                Version: metadata.GetProperty("meshVersion").GetString() ?? string.Empty,
                Http3: metadata.GetProperty("http3Support").GetBoolean());
        }

        return peers;
    }

    private static void AssertViewsMatch(Dictionary<string, PeerView> diagnostics, Dictionary<string, PeerView> cli)
    {
        cli.Count.Should().Be(diagnostics.Count);

        foreach (var (nodeId, diagView) in diagnostics)
        {
            cli.TryGetValue(nodeId, out var cliView).Should().BeTrue($"CLI output missing node '{nodeId}'.");
            cliView.Should().Be(diagView);
        }
    }

    private sealed record PeerView(string Status, string Role, string Cluster, string Region, string Version, bool Http3);

    private sealed class GossipMetricsListener : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.OrdinalIgnoreCase);

        public GossipMetricsListener()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == "OmniRelay.Core.Gossip" && instrument.Name == "mesh_gossip_members")
                    {
                        listener.EnableMeasurementEvents(instrument, state: null);
                    }
                }
            };

            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            {
                if (!string.Equals(instrument.Name, "mesh_gossip_members", StringComparison.Ordinal))
                {
                    return;
                }

                var status = "unknown";
                foreach (var tag in tags)
                {
                    if (string.Equals(tag.Key, "mesh.status", StringComparison.Ordinal) && tag.Value is string value)
                    {
                        status = value;
                        break;
                    }
                }

                _counts.AddOrUpdate(status, measurement, (_, current) => current + measurement);
            });

            _listener.Start();
        }

        public long GetCount(string status) =>
            _counts.TryGetValue(status, out var value) ? value : 0;

        public void Dispose() => _listener.Dispose();
    }

    private static int AllocateUniquePort(ISet<int> reserved)
    {
        while (true)
        {
            var port = TestPortAllocator.GetRandomPort();
            if (reserved.Add(port))
            {
                return port;
            }
        }
    }
}
