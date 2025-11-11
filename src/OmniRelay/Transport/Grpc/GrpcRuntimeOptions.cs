using System.Diagnostics.CodeAnalysis;
using Grpc.Core.Interceptors;
using OmniRelay.Transport.Http;

namespace OmniRelay.Transport.Grpc;

/// <summary>
/// Runtime options for gRPC clients including protocol, limits, keep-alive, and interceptors.
/// </summary>
public sealed record GrpcClientRuntimeOptions
{
    public bool EnableHttp3 { get; init; }

    public Version? RequestVersion { get; init; }

    public HttpVersionPolicy? VersionPolicy { get; init; }

    public int? MaxReceiveMessageSize { get; init; }

    public int? MaxSendMessageSize { get; init; }

    public TimeSpan? KeepAlivePingDelay { get; init; }

    public TimeSpan? KeepAlivePingTimeout { get; init; }

    public HttpKeepAlivePingPolicy? KeepAlivePingPolicy { get; init; }

    public IReadOnlyList<Interceptor> Interceptors { get; init; } = [];

    public bool AllowHttp2Fallback { get; init; } = true;
}

/// <summary>
/// Runtime options for the gRPC server including message limits, errors, keep-alive, and HTTP/3.
/// </summary>
public sealed record GrpcServerRuntimeOptions
{
    public bool EnableHttp3 { get; init; }

    public int? MaxReceiveMessageSize { get; init; }

    public int? MaxSendMessageSize { get; init; }

    public TimeSpan? KeepAlivePingDelay { get; init; }

    public TimeSpan? KeepAlivePingTimeout { get; init; }

    public bool? EnableDetailedErrors { get; init; }

    private IReadOnlyList<Type> _interceptors = [];

    public IReadOnlyList<Type> Interceptors
    {
        get => _interceptors;
        init => _interceptors = value ?? [];
    }

    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Interceptor types are explicitly referenced via typeof() so their constructors remain linked.")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Interceptor types are explicitly referenced via typeof() so their constructors remain linked.")]
    internal IReadOnlyList<AnnotatedServerInterceptorType> AnnotatedInterceptors
    {
        get
        {
            if (_annotatedInterceptors is null)
            {
                var list = new List<AnnotatedServerInterceptorType>(_interceptors.Count);
                foreach (var type in _interceptors)
                {
                    if (type is not null)
                    {
                        var preserved = EnsureServerInterceptorType(type);
                        list.Add(new AnnotatedServerInterceptorType(preserved));
                    }
                }

                _annotatedInterceptors = list;
            }

            return _annotatedInterceptors;
        }
    }

    private IReadOnlyList<AnnotatedServerInterceptorType>? _annotatedInterceptors;

    public TimeSpan? ServerStreamWriteTimeout { get; init; }

    public TimeSpan? DuplexWriteTimeout { get; init; }

    public int? ServerStreamMaxMessageBytes { get; init; }

    public int? DuplexMaxMessageBytes { get; init; }

    public Http3RuntimeOptions? Http3 { get; init; }

    private static Type EnsureServerInterceptorType([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicMethods)] Type type) =>
        type ?? throw new ArgumentNullException(nameof(type));
}

internal readonly struct AnnotatedServerInterceptorType
{
    public AnnotatedServerInterceptorType([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicMethods)] Type type) =>
        Type = type ?? throw new ArgumentNullException(nameof(type));

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicMethods)]
    [field: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicMethods)]
    public Type Type { get; }
}
