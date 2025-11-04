namespace YARPCore.Transport.Http;

public static class HttpTransportHeaders
{
    public const string Procedure = "Rpc-Procedure";
    public const string Caller = "Rpc-Caller";
    public const string Encoding = "Rpc-Encoding";
    public const string ShardKey = "Rpc-Shard-Key";
    public const string RoutingKey = "Rpc-Routing-Key";
    public const string RoutingDelegate = "Rpc-Routing-Delegate";
    public const string TtlMs = "Rpc-Ttl-Ms";
    public const string Deadline = "Rpc-Deadline";
    public const string Transport = "Rpc-Transport";
    public const string Status = "Rpc-Status";
    public const string ErrorCode = "Rpc-Error-Code";
    public const string ErrorMessage = "Rpc-Error-Message";
}
