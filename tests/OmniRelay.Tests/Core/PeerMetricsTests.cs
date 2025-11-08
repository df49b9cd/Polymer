using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Hugo.Policies;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Peers;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Core;

public sealed class PeerMetricsTests : IDisposable
{
    private readonly MetricListener _listener;

    private readonly ConcurrentBag<MetricMeasurement> _measurements = [];
    private static readonly string[] InstrumentsToTrack =
    [
        "yarpcore.peer.inflight",
        "yarpcore.peer.successes",
        "yarpcore.peer.failures",
        "yarpcore.peer.lease_rejected",
        "yarpcore.peer.pool_exhausted",
        "yarpcore.retry.scheduled",
        "yarpcore.retry.exhausted",
        "yarpcore.retry.succeeded"
    ];

    public PeerMetricsTests()
    {
        _listener = new MetricListener(_measurements);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task LeaseSuccess_RecordsInflightAndSuccessMetrics()
    {
        var peer = new CapturingPeer("peer-1");
        var chooser = new RoundRobinPeerChooser(peer);
        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");

        var leaseResult = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        Assert.True(leaseResult.IsSuccess);

        await using var lease = leaseResult.Value;
        lease.MarkSuccess();

        await lease.DisposeAsync();

        var inflight = GetMeasurements("yarpcore.peer.inflight");
        Assert.Contains(inflight, m => m.Value == 1 && HasTag(m, "rpc.peer", "peer-1"));
        Assert.Contains(inflight, m => m.Value == -1 && HasTag(m, "rpc.peer", "peer-1"));

        var successes = GetMeasurements("yarpcore.peer.successes");
        Assert.Contains(successes, m => m.Value == 1 && HasTag(m, "rpc.peer", "peer-1"));
        Assert.DoesNotContain(GetMeasurements("yarpcore.peer.failures"), m => HasTag(m, "rpc.peer", "peer-1"));
    }

    [Fact]
    public async Task LeaseFailure_RecordsFailureMetric()
    {
        var peer = new CapturingPeer("peer-fail");
        var chooser = new RoundRobinPeerChooser(peer);
        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");

        var leaseResult = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        Assert.True(leaseResult.IsSuccess);

        await using var lease = leaseResult.Value;
        // Do not mark success so the lease reports failure on dispose.
        await lease.DisposeAsync();

        var failures = GetMeasurements("yarpcore.peer.failures");
        Assert.Contains(failures, m => m.Value == 1 && HasTag(m, "rpc.peer", "peer-fail"));
    }

    [Fact]
    public async Task BusyPeer_RecordsRejectionsAndPoolExhaustion()
    {
        var peer = new BusyPeer("peer-busy");
        var chooser = new RoundRobinPeerChooser(peer);
        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");

        var leaseResult = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        Assert.True(leaseResult.IsFailure);

        Assert.Contains(GetMeasurements("yarpcore.peer.lease_rejected"), m => HasTag(m, "rpc.peer", "peer-busy"));
        Assert.Contains(GetMeasurements("yarpcore.peer.pool_exhausted"), m => HasTag(m, "rpc.transport", "grpc"));
    }

    [Fact]
    public async Task RetryMiddleware_EmitsRetryMetrics()
    {
        var options = new RetryOptions
        {
            Policy = ResultExecutionPolicy.None.WithRetry(ResultRetryPolicy.FixedDelay(maxAttempts: 3, delay: TimeSpan.Zero)),
            TimeProvider = TimeProvider.System
        };

        var middleware = new RetryMiddleware(options);

        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "unavailable", transport: "grpc");

        var attempt = 0;
        var result = await middleware.InvokeAsync(
            request,
            CancellationToken.None,
            (UnaryOutboundDelegate)((_, _) =>
            {
                attempt++;
                if (attempt < 2)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error));
                }

                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            }));

        Assert.True(result.IsSuccess);

        Assert.Contains(GetMeasurements("yarpcore.retry.scheduled"), m => HasTag(m, "error.status", nameof(OmniRelayStatusCode.Unavailable)));
        Assert.Contains(GetMeasurements("yarpcore.retry.succeeded"), m => HasTag(m, "retry.attempts", 2));
    }

    private IReadOnlyList<MetricMeasurement> GetMeasurements(string instrument) =>
        [.. _measurements.Where(m => string.Equals(m.Instrument, instrument, StringComparison.Ordinal))];

    private static bool HasTag(MetricMeasurement measurement, string key, object? expected)
    {
        foreach (var tag in measurement.Tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal) &&
                Equals(tag.Value, expected))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record MetricMeasurement(string Instrument, long Value, KeyValuePair<string, object?>[] Tags);

    private sealed class MetricListener : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly HashSet<string> _instruments;

        public MetricListener(ConcurrentBag<MetricMeasurement> sink)
        {
            _instruments = InstrumentsToTrack.ToHashSet(StringComparer.Ordinal);
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == "OmniRelay.Core.Peers" &&
                        _instruments.Contains(instrument.Name))
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                }
            };

            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                if (!_instruments.Contains(instrument.Name))
                {
                    return;
                }

                sink.Add(new MetricMeasurement(
                    instrument.Name,
                    measurement,
                    tags.ToArray()));
            });

            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class CapturingPeer(string identifier) : IPeer
    {
        private readonly string _identifier = identifier;
        private int _inflight;

        public string Identifier => _identifier;

        public PeerStatus Status => new(PeerState.Available, Volatile.Read(ref _inflight), null, null);

        public bool TryAcquire(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _inflight);
            return true;
        }

        public void Release(bool success) => Interlocked.Decrement(ref _inflight);
    }

    private sealed class BusyPeer(string identifier) : IPeer
    {
        private readonly string _identifier = identifier;

        public string Identifier => _identifier;

        public PeerStatus Status => new(PeerState.Available, 1, null, null);

        public bool TryAcquire(CancellationToken cancellationToken = default) => false;

        public void Release(bool success)
        {
        }
    }
}
