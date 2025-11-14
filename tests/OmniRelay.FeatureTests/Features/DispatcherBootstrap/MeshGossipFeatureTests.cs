using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniRelay.Core.Gossip;
using OmniRelay.FeatureTests.Fixtures;
using OmniRelay.Tests;
using OmniRelay.Tests.Support;
using Xunit;

namespace OmniRelay.FeatureTests.Features.DispatcherBootstrap;

[Collection(nameof(FeatureTestCollection))]
public sealed class MeshGossipFeatureTests(FeatureTestApplication application) : IAsyncLifetime
{
    private readonly FeatureTestApplication _application = application;
    private readonly List<MeshGossipHost> _extraHosts = new();

    [Fact(DisplayName = "Gossip cluster surfaces consistent peers via CLI, diagnostics, and metrics")]
    public async Task GossipClusterHasConsistentDiagnosticsAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        using var metrics = new GossipMetricsListener();

        var dispatcherAgent = _application.Services.GetRequiredService<IMeshGossipAgent>();
        Assert.True(dispatcherAgent.IsEnabled, "Feature test dispatcher gossip agent is not enabled.");
        var loggerFactory = _application.Services.GetRequiredService<ILoggerFactory>();
        var dispatcherPort = _application.GossipPort;
        var reservedPorts = new HashSet<int>
        {
            _application.ControlPlanePort,
            _application.HttpInboundPort,
            dispatcherPort
        };
        var workerPort = AllocateUniquePort(reservedPorts);
        var gatewayPort = AllocateUniquePort(reservedPorts);

        var workerHost = await StartPeerAsync(
            "feature-tests-worker",
            "worker",
            FeatureTestMeshOptions.WorkerRack,
            workerPort,
            seedPeers: new[]
            {
                $"127.0.0.1:{dispatcherPort}",
                $"127.0.0.1:{gatewayPort}"
            },
            loggerFactory,
            ct).ConfigureAwait(false);

        var gatewayHost = await StartPeerAsync(
            "feature-tests-gateway",
            "gateway",
            FeatureTestMeshOptions.GatewayRack,
            gatewayPort,
            seedPeers: new[]
            {
                $"127.0.0.1:{dispatcherPort}",
                $"127.0.0.1:{workerPort}"
            },
            loggerFactory,
            ct).ConfigureAwait(false);

        var convergenceSucceeded = await WaitForConditionAsync(
            () => AllAlive(dispatcherAgent, workerHost.LocalMetadata.NodeId, gatewayHost.LocalMetadata.NodeId) &&
                  AllAlive(workerHost, dispatcherAgent.LocalMetadata.NodeId, gatewayHost.LocalMetadata.NodeId) &&
                  AllAlive(gatewayHost, dispatcherAgent.LocalMetadata.NodeId, workerHost.LocalMetadata.NodeId),
            TimeSpan.FromSeconds(15),
            ct).ConfigureAwait(false);

        Assert.True(
            convergenceSucceeded,
            $"Mesh peers did not converge.{Environment.NewLine}{DescribeSnapshots(dispatcherAgent, workerHost, gatewayHost)}");

        var diagnosticsSnapshot = await ReadPeerDiagnosticsAsync(ct).ConfigureAwait(false);
        var cliSnapshot = await ReadCliPeersAsync(ct).ConfigureAwait(false);

        AssertViewsMatch(diagnosticsSnapshot, cliSnapshot);

        await AssertMetricsAsync(metrics, expectedAlive: 3, ct).ConfigureAwait(false);
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

    private async Task<MeshGossipHost> StartPeerAsync(
        string nodeId,
        string role,
        string rack,
        int port,
        IReadOnlyList<string> seedPeers,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var metadataLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rack"] = rack
        };

        var options = new MeshGossipOptions
        {
            Enabled = true,
            NodeId = nodeId,
            Role = role,
            ClusterId = FeatureTestMeshOptions.ClusterId,
            Region = FeatureTestMeshOptions.Region,
            MeshVersion = FeatureTestMeshOptions.MeshVersion,
            BindAddress = "127.0.0.1",
            AdvertiseHost = "127.0.0.1",
            Port = port,
            AdvertisePort = port,
            Interval = TimeSpan.FromMilliseconds(500),
            SuspicionInterval = TimeSpan.FromSeconds(2),
            PingTimeout = TimeSpan.FromMilliseconds(500),
            Fanout = 2,
            RetransmitLimit = 2,
            MetadataRefreshPeriod = TimeSpan.FromSeconds(2),
            SeedPeers = seedPeers.ToList(),
            Labels = metadataLabels
        };

        options.Tls.CertificateData = _application.Certificate.CertificateData;
        options.Tls.CertificatePassword = _application.Certificate.Password;
        options.Tls.AllowUntrustedCertificates = true;
        options.Tls.CheckCertificateRevocation = false;

        var logger = loggerFactory.CreateLogger<MeshGossipHost>();
        var host = new MeshGossipHost(options, metadata: null, logger, loggerFactory);

        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        _extraHosts.Add(host);
        return host;
    }

    private static bool AllAlive(IMeshGossipAgent agent, params string[] nodeIds) =>
        nodeIds.All(nodeId => HasStatus(agent, nodeId, MeshGossipMemberStatus.Alive));

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

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
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
        Assert.Equal(diagnostics.Count, cli.Count);

        foreach (var (nodeId, diagView) in diagnostics)
        {
            Assert.True(cli.TryGetValue(nodeId, out var cliView), $"CLI output missing node '{nodeId}'.");
            Assert.Equal(diagView, cliView);
        }
    }

    private async Task<Dictionary<string, PeerView>> ReadPeerDiagnosticsAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri(_application.ControlPlaneBaseAddress) };
        using var diagnostics = await client.GetFromJsonAsync<JsonDocument>("control/peers", cancellationToken).ConfigureAwait(false);
        Assert.NotNull(diagnostics);
        return ExtractPeers(diagnostics!);
    }

    private async Task<Dictionary<string, PeerView>> ReadCliPeersAsync(CancellationToken cancellationToken)
    {
        var result = await CliCommandRunner.RunAsync(
            $"mesh peers list --url {_application.ControlPlaneBaseAddress} --format json --timeout 10s",
            cancellationToken).ConfigureAwait(false);

        Assert.True(result.ExitCode == 0, $"CLI command failed: {result.Stderr}");
        Assert.False(string.IsNullOrWhiteSpace(result.Stdout), "CLI command returned no output.");

        using var cliDocument = JsonDocument.Parse(result.Stdout);
        return ExtractPeers(cliDocument);
    }

    private static async Task AssertMetricsAsync(GossipMetricsListener listener, int expectedAlive, CancellationToken cancellationToken)
    {
        var success = await WaitForConditionAsync(
            () => listener.GetCount("alive") >= expectedAlive,
            TimeSpan.FromSeconds(5),
            cancellationToken).ConfigureAwait(false);

        Assert.True(success, "mesh_gossip_members alive metric did not reach the expected value.");
        Assert.Equal(0, listener.GetCount("suspect"));
        Assert.Equal(0, listener.GetCount("left"));
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
