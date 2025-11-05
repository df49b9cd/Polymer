namespace OmniRelay.Configuration.Models;

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
}

public sealed class InboundsConfiguration
{
    public IList<HttpInboundConfiguration> Http { get; } = [];

    public IList<GrpcInboundConfiguration> Grpc { get; } = [];
}

public sealed class HttpInboundConfiguration
{
    public string? Name { get; set; }

    public IList<string> Urls { get; } = [];

    public HttpServerRuntimeConfiguration Runtime { get; init; } = new();

    public HttpServerTlsConfiguration Tls { get; init; } = new();
}

public sealed class GrpcInboundConfiguration
{
    public string? Name { get; set; }

    public IList<string> Urls { get; } = [];

    public GrpcServerRuntimeConfiguration Runtime { get; init; } = new();

    public GrpcServerTlsConfiguration Tls { get; init; } = new();

    public GrpcTelemetryConfiguration Telemetry { get; init; } = new();
}

public sealed class GrpcServerRuntimeConfiguration
{
    public int? MaxReceiveMessageSize { get; set; }

    public int? MaxSendMessageSize { get; set; }

    public bool? EnableDetailedErrors { get; set; }

    public TimeSpan? KeepAlivePingDelay { get; set; }

    public TimeSpan? KeepAlivePingTimeout { get; set; }

    public IList<string> Interceptors { get; } = [];
}

public sealed class GrpcServerTlsConfiguration
{
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    public bool? CheckCertificateRevocation { get; set; }

    public string? ClientCertificateMode { get; set; }
}

public sealed class GrpcTelemetryConfiguration
{
    public bool? EnableServerLogging { get; set; }

    public bool? EnableClientLogging { get; set; }
}

public sealed class HttpServerRuntimeConfiguration
{
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
}

public sealed class HttpServerTlsConfiguration
{
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    public string? ClientCertificateMode { get; set; }

    public bool? CheckCertificateRevocation { get; set; }
}

public sealed class ServiceOutboundConfiguration
{
    public RpcOutboundConfiguration? Unary { get; set; }

    public RpcOutboundConfiguration? Oneway { get; set; }

    public RpcOutboundConfiguration? Stream { get; set; }

    public RpcOutboundConfiguration? ClientStream { get; set; }

    public RpcOutboundConfiguration? Duplex { get; set; }
}

public sealed class RpcOutboundConfiguration
{
    public IList<HttpOutboundTargetConfiguration> Http { get; } = [];

    public IList<GrpcOutboundTargetConfiguration> Grpc { get; } = [];
}

public sealed class HttpOutboundTargetConfiguration
{
    public string? Key { get; set; }

    public string? Url { get; set; }

    public string? ClientName { get; set; }
}

public sealed class GrpcOutboundTargetConfiguration
{
    public string? Key { get; set; }

    public IList<string> Addresses { get; } = [];

    public string? RemoteService { get; set; }

    public string? PeerChooser { get; set; }

    public PeerSpecConfiguration? Peer { get; set; }

    public PeerCircuitBreakerConfiguration CircuitBreaker { get; init; } = new();

    public GrpcClientRuntimeConfiguration Runtime { get; init; } = new();

    public GrpcClientTlsConfiguration Tls { get; init; } = new();

    public GrpcTelemetryConfiguration Telemetry { get; init; } = new();
}

public sealed class PeerCircuitBreakerConfiguration
{
    public TimeSpan? BaseDelay { get; set; }

    public TimeSpan? MaxDelay { get; set; }

    public int? FailureThreshold { get; set; }

    public int? HalfOpenMaxAttempts { get; set; }

    public int? HalfOpenSuccessThreshold { get; set; }
}

public sealed class GrpcClientRuntimeConfiguration
{
    public int? MaxReceiveMessageSize { get; set; }

    public int? MaxSendMessageSize { get; set; }

    public TimeSpan? KeepAlivePingDelay { get; set; }

    public TimeSpan? KeepAlivePingTimeout { get; set; }

    public IList<string> Interceptors { get; } = [];
}

public sealed class GrpcClientTlsConfiguration
{
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    public string? TargetNameOverride { get; set; }

    public bool? AllowUntrustedCertificates { get; set; }
}

public sealed class MiddlewareConfiguration
{
    public MiddlewareStackConfiguration Inbound { get; init; } = new();

    public MiddlewareStackConfiguration Outbound { get; init; } = new();
}

public sealed class MiddlewareStackConfiguration
{
    public IList<string> Unary { get; } = [];

    public IList<string> Oneway { get; } = [];

    public IList<string> Stream { get; } = [];

    public IList<string> ClientStream { get; } = [];

    public IList<string> Duplex { get; } = [];
}

public sealed class LoggingConfiguration
{
    public string? Level { get; set; }

    public IDictionary<string, string> Overrides { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class PeerSpecConfiguration
{
    public string? Spec { get; set; }

    public IDictionary<string, string?> Settings { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

public sealed class EncodingsConfiguration
{
    public JsonEncodingConfiguration Json { get; init; } = new();
}

public sealed class JsonEncodingConfiguration
{
    public IDictionary<string, JsonSerializerProfileConfiguration> Profiles { get; init; } =
        new Dictionary<string, JsonSerializerProfileConfiguration>(StringComparer.OrdinalIgnoreCase);

    public IList<JsonCodecRegistrationConfiguration> Inbound { get; } = [];

    public IList<JsonCodecRegistrationConfiguration> Outbound { get; } = [];
}

public sealed class JsonSerializerProfileConfiguration
{
    public JsonSerializerOptionsConfiguration Options { get; init; } = new();

    public IList<string> Converters { get; } = [];

    public string? Context { get; set; }
}

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
