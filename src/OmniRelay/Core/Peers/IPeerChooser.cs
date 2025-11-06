using Hugo;

namespace OmniRelay.Core.Peers;

/// <summary>
/// Strategy interface that chooses a peer for a given request.
/// </summary>
public interface IPeerChooser
{
    /// <summary>
    /// Acquires a lease to a selected peer for the given request metadata.
    /// </summary>
    ValueTask<Result<PeerLease>> AcquireAsync(RequestMeta meta, CancellationToken cancellationToken = default);
}
