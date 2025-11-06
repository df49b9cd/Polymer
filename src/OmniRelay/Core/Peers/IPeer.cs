namespace OmniRelay.Core.Peers;

/// <summary>
/// Represents a remote endpoint that can process requests and report status.
/// </summary>
public interface IPeer
{
    /// <summary>Gets a stable peer identifier (e.g., host:port).</summary>
    string Identifier { get; }

    /// <summary>Gets the current status snapshot for the peer.</summary>
    PeerStatus Status { get; }

    /// <summary>Attempts to acquire a lease for sending a request to this peer.</summary>
    bool TryAcquire(CancellationToken cancellationToken = default);

    /// <summary>Releases a previously acquired lease, reporting the outcome.</summary>
    void Release(bool success);
}
