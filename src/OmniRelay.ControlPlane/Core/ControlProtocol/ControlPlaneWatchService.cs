using Grpc.Core;
using OmniRelay.Protos.Control;

namespace OmniRelay.ControlPlane.ControlProtocol;

/// <summary>
/// Control-plane watch service (delta + snapshot). Placeholder implementation; to be wired to real config sources.
/// </summary>
public sealed class ControlPlaneWatchService : ControlPlaneWatch.ControlPlaneWatchBase
{
    public override Task<ControlSnapshotResponse> Snapshot(ControlSnapshotRequest request, ServerCallContext context)
    {
        // Minimal stub to satisfy clients; return empty snapshot.
        return Task.FromResult(new ControlSnapshotResponse { Version = "0", Payload = Google.Protobuf.ByteString.Empty });
    }

    public override async Task Watch(ControlWatchRequest request, IServerStreamWriter<ControlWatchResponse> responseStream, ServerCallContext context)
    {
        // Stub: immediately complete with a single empty response.
        await responseStream.WriteAsync(new ControlWatchResponse { Version = "0", FullSnapshot = true }).ConfigureAwait(false);
    }
}
