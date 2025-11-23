namespace OmniRelay.Dispatcher;

public interface IGrpcResourceLeaseReplicatorClient
{
    ValueTask PublishAsync(ResourceLeaseReplicationEventMessage message, CancellationToken cancellationToken);
}

internal sealed class GrpcResourceLeaseReplicatorClientAdapter(
    ResourceLeaseReplicatorGrpc.ResourceLeaseReplicatorGrpcClient client)
    : IGrpcResourceLeaseReplicatorClient
{
    private readonly ResourceLeaseReplicatorGrpc.ResourceLeaseReplicatorGrpcClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public async ValueTask PublishAsync(ResourceLeaseReplicationEventMessage message, CancellationToken cancellationToken)
    {
        await _client.PublishAsync(message, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
