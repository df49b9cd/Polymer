namespace Polymer.Transport.Grpc;

internal static class GrpcTransportConstants
{
    public const string ServiceName = "polymer.Dispatcher";
    public const string UnaryMethod = "CallUnary";
    public const string MetadataProcedure = "rpc-procedure";
    public const string MetadataEncoding = "rpc-encoding";
    public const string MetadataCaller = "rpc-caller";
    public const string MetadataShardKey = "rpc-shard-key";
    public const string MetadataRoutingKey = "rpc-routing-key";
    public const string MetadataRoutingDelegate = "rpc-routing-delegate";
    public const string MetadataTtl = "rpc-ttl-ms";
    public const string MetadataDeadline = "rpc-deadline";
}
