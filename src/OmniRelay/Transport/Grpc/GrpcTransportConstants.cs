namespace OmniRelay.Transport.Grpc;

/// <summary>
/// Constants for gRPC transport header and trailer names and common identifiers.
/// </summary>
internal static class GrpcTransportConstants
{
    public const string ServiceNameHeader = "rpc-service";
    public const string ProcedureHeader = "rpc-procedure";
    public const string EncodingHeader = "rpc-encoding";
    public const string CallerHeader = "rpc-caller";
    public const string ShardKeyHeader = "rpc-shard-key";
    public const string RoutingKeyHeader = "rpc-routing-key";
    public const string RoutingDelegateHeader = "rpc-routing-delegate";
    public const string TtlHeader = "rpc-ttl-ms";
    public const string DeadlineHeader = "rpc-deadline";
    public const string GrpcEncodingHeader = "grpc-encoding";
    public const string GrpcAcceptEncodingHeader = "grpc-accept-encoding";

    public const string TransportName = "grpc";

    public const string StatusTrailer = "omnirelay-status";
    public const string ErrorCodeTrailer = "omnirelay-error-code";
    public const string ErrorMessageTrailer = "omnirelay-error-message";
    public const string EncodingTrailer = "omnirelay-encoding";
    public const string TransportTrailer = "omnirelay-transport";
}
