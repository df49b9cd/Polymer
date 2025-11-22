using Grpc.Core;
using Grpc.Net.Client;
using OmniRelay.Protos.Control;
using System.Runtime.CompilerServices;

namespace OmniRelay.ControlPlane.ControlProtocol;

public sealed class ControlPlaneWatchClient : IControlPlaneWatchClient, IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly ControlPlaneWatch.ControlPlaneWatchClient _client;

    public ControlPlaneWatchClient(GrpcChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _client = new ControlPlaneWatch.ControlPlaneWatchClient(channel);
    }

    public async IAsyncEnumerable<ControlWatchResponse> WatchAsync(ControlWatchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var call = _client.Watch(request, cancellationToken: cancellationToken);
        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            yield return call.ResponseStream.Current;
        }
    }

    public Task<ControlSnapshotResponse> SnapshotAsync(ControlSnapshotRequest request, CancellationToken cancellationToken = default) =>
        _client.SnapshotAsync(request, cancellationToken: cancellationToken).ResponseAsync;

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        return ValueTask.CompletedTask;
    }
}
