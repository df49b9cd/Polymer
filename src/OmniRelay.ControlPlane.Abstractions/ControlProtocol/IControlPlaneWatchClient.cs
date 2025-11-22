using OmniRelay.Protos.Control;

namespace OmniRelay.ControlPlane.ControlProtocol;

public interface IControlPlaneWatchClient
{
    IAsyncEnumerable<ControlWatchResponse> WatchAsync(ControlWatchRequest request, CancellationToken cancellationToken = default);
    Task<ControlSnapshotResponse> SnapshotAsync(ControlSnapshotRequest request, CancellationToken cancellationToken = default);
}
