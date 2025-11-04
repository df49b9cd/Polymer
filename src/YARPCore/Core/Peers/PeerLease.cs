using System.Diagnostics;

namespace YARPCore.Core.Peers;

public sealed class PeerLease : IAsyncDisposable
{
    private readonly IPeer _peer;
    private readonly string _peerIdentifier;
    private bool _released;
    private bool _success;
    private readonly long _startTimestamp;

    internal PeerLease(IPeer peer, RequestMeta meta)
    {
        _peer = peer ?? throw new ArgumentNullException(nameof(peer));
        Meta = meta ?? throw new ArgumentNullException(nameof(meta));
        _success = false;
        _peerIdentifier = _peer.Identifier;
        _startTimestamp = Stopwatch.GetTimestamp();
        PeerMetrics.RecordLeaseAcquired(Meta, _peerIdentifier);
    }

    public IPeer Peer => _peer;

    public RequestMeta Meta { get; }

    public void MarkSuccess() => _success = true;

    public void MarkFailure() => _success = false;

    public ValueTask DisposeAsync()
    {
        if (_released)
        {
            return ValueTask.CompletedTask;
        }

        _released = true;
        var elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
        PeerMetrics.RecordLeaseReleased(Meta, _peerIdentifier, _success, elapsed);
        if (_peer is IPeerTelemetry telemetry)
        {
            telemetry.RecordLeaseResult(_success, elapsed);
        }

        _peer.Release(_success);
        return ValueTask.CompletedTask;
    }
}
