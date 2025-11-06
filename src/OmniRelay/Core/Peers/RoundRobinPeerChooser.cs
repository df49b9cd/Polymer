using System.Collections.Immutable;
using Hugo;
using OmniRelay.Errors;
using static Hugo.Go;

namespace OmniRelay.Core.Peers;

/// <summary>
/// Chooses peers in a round-robin fashion, skipping busy peers.
/// </summary>
public sealed class RoundRobinPeerChooser : IPeerChooser
{
    private readonly ImmutableArray<IPeer> _peers;
    private int _next = -1;

    public RoundRobinPeerChooser(params IPeer[] peers)
    {
        ArgumentNullException.ThrowIfNull(peers);

        _peers = [.. peers];
    }

    public RoundRobinPeerChooser(ImmutableArray<IPeer> peers)
    {
        _peers = peers;
    }

    public ValueTask<Result<PeerLease>> AcquireAsync(RequestMeta meta, CancellationToken cancellationToken = default)
    {
        if (_peers.IsDefaultOrEmpty)
        {
            var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "No peers are registered for the requested service.", transport: meta.Transport ?? "unknown");
            return ValueTask.FromResult(Err<PeerLease>(error));
        }

        var length = _peers.Length;
        for (var attempt = 0; attempt < length; attempt++)
        {
            var index = Interlocked.Increment(ref _next);
            var resolved = _peers[index % length];

            if (resolved.TryAcquire(cancellationToken))
            {
                return ValueTask.FromResult(Ok(new PeerLease(resolved, meta)));
            }

            PeerMetrics.RecordLeaseRejected(meta, resolved.Identifier, "busy");
        }

        PeerMetrics.RecordPoolExhausted(meta);
        var exhausted = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.ResourceExhausted, "All peers are busy.", transport: meta.Transport ?? "unknown");
        return ValueTask.FromResult(Err<PeerLease>(exhausted));
    }
}
