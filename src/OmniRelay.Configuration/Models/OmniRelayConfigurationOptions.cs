namespace OmniRelay.Configuration.Models;

/// <summary>
/// Root options bound from configuration to construct a dispatcher, including inbounds, outbounds,
/// middleware, logging, encodings, and diagnostics.
/// </summary>
public sealed class OmniRelayConfigurationOptions
{
    public string? Service { get; set; }

    public InboundsConfiguration Inbounds { get; init; } = new();

    public IDictionary<string, ServiceOutboundConfiguration> Outbounds { get; init; } =
        new Dictionary<string, ServiceOutboundConfiguration>(StringComparer.OrdinalIgnoreCase);

    public MiddlewareConfiguration Middleware { get; init; } = new();

    public LoggingConfiguration Logging { get; init; } = new();

    public EncodingsConfiguration Encodings { get; init; } = new();

    public DiagnosticsConfiguration Diagnostics { get; init; } = new();

    public ShardingConfiguration Sharding { get; init; } = new();

    public SecurityConfiguration Security { get; init; } = new();

    public TransportPolicyConfiguration TransportPolicy { get; init; } = new();

    /// <summary>
    /// Native AOT settings that restrict reflection-based configuration and enforce the use of known, generator-friendly components.
    /// </summary>
    public NativeAotConfiguration NativeAot { get; init; } = new();
}

/// <summary>Inbound transport configuration for HTTP and gRPC servers.</summary>
public sealed class InboundsConfiguration
{
    public IList<HttpInboundConfiguration> Http { get; } = [];

    public IList<GrpcInboundConfiguration> Grpc { get; } = [];
}

/// <summary>HTTP inbound settings (URLs, runtime, TLS).</summary>
public sealed class HttpInboundConfiguration
{
    public string? Name { get; set; }

    public IList<string> Urls { get; } = [];

    public HttpServerRuntimeConfiguration Runtime { get; init; } = new();

    public HttpServerTlsConfiguration Tls { get; init; } = new();
}

/// <summary>gRPC inbound settings (URLs, runtime, TLS, telemetry).</summary>
public sealed class GrpcInboundConfiguration
{
    public string? Name { get; set; }

    public IList<string> Urls { get; } = [];

    public GrpcServerRuntimeConfiguration Runtime { get; init; } = new();

    public GrpcServerTlsConfiguration Tls { get; init; } = new();

    public GrpcTelemetryConfiguration Telemetry { get; init; } = new();
}

/// <summary>gRPC server runtime limits and HTTP/3 tuning options.</summary>
public sealed class GrpcServerRuntimeConfiguration
{
    public bool? EnableHttp3 { get; set; }

    public int? MaxReceiveMessageSize { get; set; }

    public int? MaxSendMessageSize { get; set; }

    public bool? EnableDetailedErrors { get; set; }

    public TimeSpan? KeepAlivePingDelay { get; set; }

    public TimeSpan? KeepAlivePingTimeout { get; set; }

    public IList<string> Interceptors { get; } = [];

    public TimeSpan? ServerStreamWriteTimeout { get; set; }

    public TimeSpan? DuplexWriteTimeout { get; set; }

    public int? ServerStreamMaxMessageBytes { get; set; }

    public int? DuplexMaxMessageBytes { get; set; }

    public Http3ServerRuntimeConfiguration Http3 { get; init; } = new();
}

/// <summary>TLS configuration for the gRPC server.</summary>
public sealed class GrpcServerTlsConfiguration
{
    public string? CertificatePath { get; set; }

    public string? CertificateData { get; set; }

    public string? CertificateDataSecret { get; set; }

    public string? CertificatePassword { get; set; }

    public string? CertificatePasswordSecret { get; set; }

    public bool? CheckCertificateRevocation { get; set; }

    public string? ClientCertificateMode { get; set; }
}

/// <summary>Enables basic gRPC transport logging for server and client.</summary>
public sealed class GrpcTelemetryConfiguration
{
    public bool? EnableServerLogging { get; set; }

    public bool? EnableClientLogging { get; set; }
}

/// <summary>HTTP server runtime limits and optional HTTP/3 tuning options.</summary>
public sealed class HttpServerRuntimeConfiguration
{
    public bool? EnableHttp3 { get; set; }

    public long? MaxRequestBodySize { get; set; }

    public long? MaxInMemoryDecodeBytes { get; set; }

    public int? MaxRequestLineSize { get; set; }

    public int? MaxRequestHeadersTotalSize { get; set; }

    public TimeSpan? KeepAliveTimeout { get; set; }

    public TimeSpan? RequestHeadersTimeout { get; set; }

    public TimeSpan? ServerStreamWriteTimeout { get; set; }

    public TimeSpan? DuplexWriteTimeout { get; set; }

    public int? ServerStreamMaxMessageBytes { get; set; }

    public int? DuplexMaxFrameBytes { get; set; }

    public Http3ServerRuntimeConfiguration Http3 { get; init; } = new();
}

/// <summary>HTTP/3 (QUIC) specific runtime options for servers.</summary>
public sealed class Http3ServerRuntimeConfiguration
{
    public bool? EnableAltSvc { get; set; }

    public TimeSpan? IdleTimeout { get; set; }

    public TimeSpan? KeepAliveInterval { get; set; }

    public int? MaxBidirectionalStreams { get; set; }

    public int? MaxUnidirectionalStreams { get; set; }
}

/// <summary>TLS configuration for the HTTP server.</summary>
public sealed class HttpServerTlsConfiguration
{
    public string? CertificatePath { get; set; }

    public string? CertificateData { get; set; }

    public string? CertificateDataSecret { get; set; }

    public string? CertificatePassword { get; set; }

    public string? CertificatePasswordSecret { get; set; }

    public string? ClientCertificateMode { get; set; }

    public bool? CheckCertificateRevocation { get; set; }
}

/// <summary>Outbound transport sets for a remote service, per RPC shape.</summary>
public sealed class ServiceOutboundConfiguration
{
    public RpcOutboundConfiguration? Unary { get; set; }

    public RpcOutboundConfiguration? Oneway { get; set; }

    public RpcOutboundConfiguration? Stream { get; set; }

    public RpcOutboundConfiguration? ClientStream { get; set; }

    public RpcOutboundConfiguration? Duplex { get; set; }
}

/// <summary>Per-shape outbound transport choices (HTTP, gRPC).</summary>
public sealed class RpcOutboundConfiguration
{
    public IList<HttpOutboundTargetConfiguration> Http { get; } = [];

    public IList<GrpcOutboundTargetConfiguration> Grpc { get; } = [];
}

/// <summary>HTTP outbound binding with optional named HttpClient and runtime options.</summary>
public sealed class HttpOutboundTargetConfiguration
{
    public string? Key { get; set; }

    public string? Url { get; set; }

    public string? ClientName { get; set; }

    public HttpClientRuntimeConfiguration Runtime { get; init; } = new();
}

/// <summary>HTTP client runtime options (HTTP/3 negotiation and version policy).</summary>
public sealed class HttpClientRuntimeConfiguration
{
    public bool? EnableHttp3 { get; set; }

    public string? RequestVersion { get; set; }

    public string? VersionPolicy { get; set; }
}

/// <summary>gRPC outbound binding with endpoints, peer configuration, and runtime/tls/telemetry.</summary>
public sealed class GrpcOutboundTargetConfiguration
{
    public string? Key { get; set; }

    public IList<string> Addresses { get; } = [];

    // Optional richer endpoint entries with protocol capabilities. When specified, this takes
    // precedence over simple string addresses for routing preference decisions.
    public IList<GrpcEndpointConfiguration> Endpoints { get; } = [];

    public string? RemoteService { get; set; }

    public string? PeerChooser { get; set; }

    public PeerSpecConfiguration? Peer { get; set; }

    public PeerCircuitBreakerConfiguration CircuitBreaker { get; init; } = new();

    public GrpcClientRuntimeConfiguration Runtime { get; init; } = new();

    public GrpcClientTlsConfiguration Tls { get; init; } = new();

    public GrpcTelemetryConfiguration Telemetry { get; init; } = new();
}

/// <summary>Describes a gRPC endpoint and whether it supports HTTP/3.</summary>
public sealed class GrpcEndpointConfiguration
{
    public string? Address { get; set; }

    // Indicates whether this endpoint is known to support HTTP/3 (QUIC).
    // When true and the client is configured to enable HTTP/3, routing will prefer these peers.
    public bool? SupportsHttp3 { get; set; }
}

/// <summary>Peer circuit breaker configuration values.</summary>
public sealed class PeerCircuitBreakerConfiguration
{
    public TimeSpan? BaseDelay { get; set; }

    public TimeSpan? MaxDelay { get; set; }

    public int? FailureThreshold { get; set; }

    public int? HalfOpenMaxAttempts { get; set; }

    public int? HalfOpenSuccessThreshold { get; set; }
}

/// <summary>gRPC client runtime limits, keep-alive pings, and interceptor list.</summary>
public sealed class GrpcClientRuntimeConfiguration
{
    public bool? EnableHttp3 { get; set; }

    public string? RequestVersion { get; set; }

    public string? VersionPolicy { get; set; }

    public int? MaxReceiveMessageSize { get; set; }

    public int? MaxSendMessageSize { get; set; }

    public TimeSpan? KeepAlivePingDelay { get; set; }

    public TimeSpan? KeepAlivePingTimeout { get; set; }

    public IList<string> Interceptors { get; } = [];
}

/// <summary>Client TLS settings for gRPC outbounds.</summary>
public sealed class GrpcClientTlsConfiguration
{
    public string? CertificatePath { get; set; }

    public string? CertificateData { get; set; }

    public string? CertificateDataSecret { get; set; }

    public string? CertificatePassword { get; set; }

    public string? CertificatePasswordSecret { get; set; }

    public string? TargetNameOverride { get; set; }

    public bool? AllowUntrustedCertificates { get; set; }
}

/// <summary>Global inbound and outbound middleware stacks.</summary>
public sealed class MiddlewareConfiguration
{
    public MiddlewareStackConfiguration Inbound { get; init; } = new();

    public MiddlewareStackConfiguration Outbound { get; init; } = new();
}

/// <summary>Procedure-shape specific middleware lists.</summary>
public sealed class MiddlewareStackConfiguration
{
    public IList<string> Unary { get; } = [];

    public IList<string> Oneway { get; } = [];

    public IList<string> Stream { get; } = [];

    public IList<string> ClientStream { get; } = [];

    public IList<string> Duplex { get; } = [];
}

/// <summary>Logging level and category overrides.</summary>
public sealed class LoggingConfiguration
{
    public string? Level { get; set; }

    public IDictionary<string, string> Overrides { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Custom peer chooser specification and settings.</summary>
public sealed class PeerSpecConfiguration
{
    public string? Spec { get; set; }

    public IDictionary<string, string?> Settings { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Codec configuration for built-in encodings (e.g., JSON).</summary>
public sealed class EncodingsConfiguration
{
    public JsonEncodingConfiguration Json { get; init; } = new();
}

/// <summary>JSON encoding profiles and codec registrations.</summary>
public sealed class JsonEncodingConfiguration
{
    public IDictionary<string, JsonSerializerProfileConfiguration> Profiles { get; init; } =
        new Dictionary<string, JsonSerializerProfileConfiguration>(StringComparer.OrdinalIgnoreCase);

    public IList<JsonCodecRegistrationConfiguration> Inbound { get; } = [];

    public IList<JsonCodecRegistrationConfiguration> Outbound { get; } = [];
}

/// <summary>Serializer options profile (options, converters, source generation context).</summary>
public sealed class JsonSerializerProfileConfiguration
{
    public JsonSerializerOptionsConfiguration Options { get; init; } = new();

    public IList<string> Converters { get; } = [];

    public string? Context { get; set; }
}

/// <summary>JSON codec registration describing procedure, types, encoding, and schema.</summary>
public sealed class JsonCodecRegistrationConfiguration
{
    public string? Service { get; set; }

    public string? Procedure { get; set; }

    public string Kind { get; set; } = "Unary";

    public string? RequestType { get; set; }

    public string? ResponseType { get; set; }

    public string? Encoding { get; set; }

    public string? Profile { get; set; }

    public JsonSerializerOptionsConfiguration Options { get; init; } = new();

    public string? Context { get; set; }

    public JsonSchemaConfiguration Schemas { get; init; } = new();

    public IList<string> Aliases { get; } = [];
}

/// <summary>Serializer options values and custom converters.</summary>
public sealed class JsonSerializerOptionsConfiguration
{
    public bool? PropertyNameCaseInsensitive { get; set; }

    public bool? WriteIndented { get; set; }

    public string? PropertyNamingPolicy { get; set; }

    public string? DefaultIgnoreCondition { get; set; }

    public IList<string> Converters { get; } = [];

    public IList<string> NumberHandling { get; } = [];

    public bool? AllowTrailingCommas { get; set; }

    public bool? IgnoreNullValues { get; set; }

    public bool? ReadCommentHandling { get; set; }
}

public sealed class JsonSchemaConfiguration
{
    public string? Request { get; set; }

    public string? Response { get; set; }
}

/// <summary>
/// Flags that opt the dispatcher bootstrapper into a trimming-safe profile.
/// When enabled, only known middleware/interceptors/converters are allowed and reflection-based binding is disabled.
/// </summary>
public sealed class NativeAotConfiguration
{
    /// <summary>Enable the native-AOT safe bootstrap path.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When true, unknown or reflection-based components (custom inbounds/outbounds, unregistered middleware/interceptors)
    /// will throw during start instead of falling back to reflection.
    /// </summary>
    public bool Strict { get; set; } = true;
}
