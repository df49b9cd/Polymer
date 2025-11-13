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
        "omnirelay.peer.inflight",
        "omnirelay.peer.successes",
        "omnirelay.peer.failures",
        "omnirelay.peer.lease_rejected",
        "omnirelay.peer.pool_exhausted",
        "omnirelay.retry.scheduled",
        "omnirelay.retry.exhausted",
        "omnirelay.retry.succeeded"
    ];

    public PeerMetricsTests()
    {
        _listener = new MetricListener(_measurements);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task LeaseSuccess_RecordsInflightAndSuccessMetrics()
    {
        var peerId = CreatePeerId("peer-success");
        var peer = new CapturingPeer(peerId);
        var chooser = new RoundRobinPeerChooser(peer);
        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");

        var leaseResult = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        leaseResult.IsSuccess.ShouldBeTrue();

        await using var lease = leaseResult.Value;
        lease.MarkSuccess();

        await lease.DisposeAsync();

        var inflight = GetMeasurements("omnirelay.peer.inflight");
        inflight.ShouldContain(m => m.Value == 1 && HasTag(m, "rpc.peer", peerId));
        inflight.ShouldContain(m => m.Value == -1 && HasTag(m, "rpc.peer", peerId));

        var successes = GetMeasurements("omnirelay.peer.successes");
        successes.ShouldContain(m => m.Value == 1 && HasTag(m, "rpc.peer", peerId));
        GetMeasurements("omnirelay.peer.failures").ShouldNotContain(m => HasTag(m, "rpc.peer", peerId));
    }

    [Fact]
    public async Task LeaseFailure_RecordsFailureMetric()
    {
        var peerId = CreatePeerId("peer-fail");
        var peer = new CapturingPeer(peerId);
        var chooser = new RoundRobinPeerChooser(peer);
        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");

        var leaseResult = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        leaseResult.IsSuccess.ShouldBeTrue();

        await using var lease = leaseResult.Value;
        // Do not mark success so the lease reports failure on dispose.
        await lease.DisposeAsync();

        var failures = GetMeasurements("omnirelay.peer.failures");
        failures.ShouldContain(m => m.Value == 1 && HasTag(m, "rpc.peer", peerId));
    }

    [Fact]
    public async Task BusyPeer_RecordsRejectionsAndPoolExhaustion()
    {
        var peerId = CreatePeerId("peer-busy");
        var peer = new BusyPeer(peerId);
        var chooser = new RoundRobinPeerChooser(peer);
        var meta = new RequestMeta(service: "svc", procedure: "echo::call", transport: "grpc");

        var leaseResult = await chooser.AcquireAsync(meta, TestContext.Current.CancellationToken);
        leaseResult.IsFailure.ShouldBeTrue();

        GetMeasurements("omnirelay.peer.lease_rejected").ShouldContain(m => HasTag(m, "rpc.peer", peerId));
        GetMeasurements("omnirelay.peer.pool_exhausted").ShouldContain(m => HasTag(m, "rpc.transport", "grpc"));
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
            (UnaryOutboundHandler)((_, _) =>
            {
                attempt++;
                if (attempt < 2)
                {
                    return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error));
                }

                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            }));

        result.IsSuccess.ShouldBeTrue();

        GetMeasurements("omnirelay.retry.scheduled").ShouldContain(m => HasTag(m, "error.status", nameof(OmniRelayStatusCode.Unavailable)));
        GetMeasurements("omnirelay.retry.succeeded").ShouldContain(m => HasTag(m, "retry.attempts", 2));
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

    private sealed record MetricMeasurement(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)
    {
        public string Instrument { get; init; } = Instrument;

        public long Value { get; init; } = Value;

        public KeyValuePair<string, object?>[] Tags { get; init; } = Tags;
    }

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
        private int _inflight;

        public string Identifier { get; } = identifier;

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
        public string Identifier { get; } = identifier;

        public PeerStatus Status => new(PeerState.Available, 1, null, null);

        public bool TryAcquire(CancellationToken cancellationToken = default) => false;

        public void Release(bool success)
        {
        }
    }

    private static string CreatePeerId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
