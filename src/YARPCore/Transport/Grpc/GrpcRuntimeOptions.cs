using Grpc.Core.Interceptors;

namespace YARPCore.Transport.Grpc;

public sealed record GrpcClientRuntimeOptions
{
    public int? MaxReceiveMessageSize { get; init; }

    public int? MaxSendMessageSize { get; init; }

    public TimeSpan? KeepAlivePingDelay { get; init; }

    public TimeSpan? KeepAlivePingTimeout { get; init; }

    public HttpKeepAlivePingPolicy? KeepAlivePingPolicy { get; init; }

    public IReadOnlyList<Interceptor> Interceptors { get; init; } = [];
}

public sealed record GrpcServerRuntimeOptions
{
    public int? MaxReceiveMessageSize { get; init; }

    public int? MaxSendMessageSize { get; init; }

    public TimeSpan? KeepAlivePingDelay { get; init; }

    public TimeSpan? KeepAlivePingTimeout { get; init; }

    public bool? EnableDetailedErrors { get; init; }

    public IReadOnlyList<Type> Interceptors { get; init; } = [];
}
