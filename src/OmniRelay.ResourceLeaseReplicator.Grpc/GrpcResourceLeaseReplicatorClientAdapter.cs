using OmniRelay.Dispatcher.Grpc;

namespace OmniRelay.Dispatcher;

public interface IGrpcResourceLeaseReplicatorClient
{
    Task PublishAsync(ResourceLeaseReplicationEventMessage message, CancellationToken cancellationToken);
}

internal sealed class GrpcResourceLeaseReplicatorClientAdapter : IGrpcResourceLeaseReplicatorClient
{
    private readonly ResourceLeaseReplicatorGrpc.ResourceLeaseReplicatorGrpcClient _client;

    public GrpcResourceLeaseReplicatorClientAdapter(ResourceLeaseReplicatorGrpc.ResourceLeaseReplicatorGrpcClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task PublishAsync(ResourceLeaseReplicationEventMessage message, CancellationToken cancellationToken)
    {
        await _client.PublishAsync(message, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
