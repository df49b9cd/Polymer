using System.Diagnostics;

namespace OmniRelay.Core.Peers;

/// <summary>
/// Represents a lease acquired for sending a request to a peer. Records metrics on release.
/// </summary>
public sealed class PeerLease : IAsyncDisposable
{
    private readonly string _peerIdentifier;
    private bool _released;
    private bool _success;
    private readonly long _startTimestamp;

    internal PeerLease(IPeer peer, RequestMeta meta)
    {
        Peer = peer ?? throw new ArgumentNullException(nameof(peer));
        Meta = meta ?? throw new ArgumentNullException(nameof(meta));
        _success = false;
        _peerIdentifier = Peer.Identifier;
        _startTimestamp = Stopwatch.GetTimestamp();
        PeerMetrics.RecordLeaseAcquired(Meta, _peerIdentifier);
    }

    /// <summary>Gets the leased peer.</summary>
    public IPeer Peer { get; }

    /// <summary>Gets the request metadata associated with the lease.</summary>
    public RequestMeta Meta { get; }

    /// <summary>Marks the lease outcome as success.</summary>
    public void MarkSuccess() => _success = true;

    /// <summary>Marks the lease outcome as failure.</summary>
    public void MarkFailure() => _success = false;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_released)
        {
            return ValueTask.CompletedTask;
        }

        _released = true;
        var elapsed = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
        PeerMetrics.RecordLeaseReleased(Meta, _peerIdentifier, _success, elapsed);
        if (Peer is IPeerTelemetry telemetry)
        {
            telemetry.RecordLeaseResult(_success, elapsed);
        }

        Peer.Release(_success);
        return ValueTask.CompletedTask;
    }
}
