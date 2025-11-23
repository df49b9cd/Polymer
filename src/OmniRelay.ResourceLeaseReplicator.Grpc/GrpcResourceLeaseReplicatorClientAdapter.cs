using Grpc.Core;
using Hugo;
using OmniRelay.Dispatcher.Grpc;
using static Hugo.Go;
using Unit = Hugo.Go.Unit;

namespace OmniRelay.Dispatcher;

public interface IGrpcResourceLeaseReplicatorClient
{
    ValueTask<Result<Unit>> PublishAsync(ResourceLeaseReplicationEventMessage message, CancellationToken cancellationToken);
}

internal sealed class GrpcResourceLeaseReplicatorClientAdapter(
    ResourceLeaseReplicatorGrpc.ResourceLeaseReplicatorGrpcClient client)
    : IGrpcResourceLeaseReplicatorClient
{
    private readonly ResourceLeaseReplicatorGrpc.ResourceLeaseReplicatorGrpcClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public async ValueTask<Result<Unit>> PublishAsync(ResourceLeaseReplicationEventMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await _client.PublishAsync(message, cancellationToken: cancellationToken).ConfigureAwait(false);
            return Ok(Unit.Value);
        }
        catch (RpcException rpc) when (rpc.StatusCode == StatusCode.Cancelled)
        {
            return Err<Unit>(Error.Canceled("gRPC publish canceled", cancellationToken)
                .WithMetadata("replication.stage", "grpc.client.publish"));
        }
        catch (Exception ex)
        {
            return Err<Unit>(Error.FromException(ex)
                .WithMetadata("replication.stage", "grpc.client.publish")
                .WithMetadata("replication.grpc.status", ex is RpcException grpc ? grpc.StatusCode.ToString() : string.Empty));
        }
    }
}
