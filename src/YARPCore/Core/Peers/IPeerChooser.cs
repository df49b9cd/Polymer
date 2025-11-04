using Hugo;

namespace YARPCore.Core.Peers;

public interface IPeerChooser
{
    ValueTask<Result<PeerLease>> AcquireAsync(RequestMeta meta, CancellationToken cancellationToken = default);
}
