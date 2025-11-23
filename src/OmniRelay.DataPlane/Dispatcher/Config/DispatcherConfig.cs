using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using OmniRelay.Transport.Http;
using System.Text.Json.Serialization;

namespace OmniRelay.Dispatcher.Config;

/// <summary>Shipping-friendly, trim-safe configuration shape for dispatcher bootstrap.</summary>
public sealed class DispatcherConfig
{
    public string Service { get; set; } = "omnirelay";
    public string Mode { get; set; } = "InProc";
    public InboundsConfig Inbounds { get; set; } = new();
    public OutboundsConfig Outbounds { get; set; } = new();
    public MiddlewareConfig Middleware { get; set; } = new();
    public EncodingConfig Encodings { get; set; } = new();
}

public sealed class InboundsConfig
{
    public List<HttpInboundConfig> Http { get; set; } = new();
    public List<GrpcInboundConfig> Grpc { get; set; } = new();
}

public sealed class HttpInboundConfig
{
    public string? Name { get; set; }
    public List<string> Urls { get; set; } = new();
    public HttpServerRuntimeOptions? Runtime { get; set; }
    public HttpServerTlsConfig? Tls { get; set; }
}

public sealed class GrpcInboundConfig
{
    public string? Name { get; set; }
    public List<string> Urls { get; set; } = new();
    public bool? EnableDetailedErrors { get; set; }
    public GrpcServerRuntimeConfig? Runtime { get; set; }
    public GrpcServerTlsConfig? Tls { get; set; }
}

public sealed class OutboundsConfig : Dictionary<string, ServiceOutboundsConfig>
{
}

public sealed class ServiceOutboundsConfig
{
    public RpcOutboundSet Http { get; set; } = new();
    public RpcOutboundSet Grpc { get; set; } = new();
}

public sealed class RpcOutboundSet
{
    public List<OutboundTarget> Unary { get; set; } = new();
    public List<OutboundTarget> Oneway { get; set; } = new();
    public List<OutboundTarget> Stream { get; set; } = new();
    public List<OutboundTarget> ClientStream { get; set; } = new();
    public List<OutboundTarget> Duplex { get; set; } = new();
}

public sealed class OutboundTarget
{
    public string? Key { get; set; }
    public string? Url { get; set; }
    public List<string> Addresses { get; set; } = new();
    public string? RemoteService { get; set; }
}

public sealed class MiddlewareConfig
{
    public MiddlewareStackConfig Inbound { get; set; } = new();
    public MiddlewareStackConfig Outbound { get; set; } = new();
}

public sealed class MiddlewareStackConfig
{
    public List<string> Unary { get; set; } = new();
    public List<string> Oneway { get; set; } = new();
    public List<string> Stream { get; set; } = new();
    public List<string> ClientStream { get; set; } = new();
    public List<string> Duplex { get; set; } = new();
}

public sealed class EncodingConfig
{
    public JsonEncodingConfig Json { get; set; } = new();
}

public sealed class JsonEncodingConfig
{
    public Dictionary<string, JsonProfileConfig> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<JsonOutboundEncodingConfig> Outbound { get; set; } = new();
}

public sealed class JsonProfileConfig
{
    public string Name { get; set; } = string.Empty;

    public bool? WriteIndented { get; set; }
}

public sealed class JsonOutboundEncodingConfig
{
    public string? Service { get; set; }

    public string? Procedure { get; set; }

    public string? Kind { get; set; }

    public string? Profile { get; set; }

    public string? Encoding { get; set; }

    /// <summary>
    /// Key of a pre-registered outbound codec in <see cref="DispatcherComponentRegistry"/>.
    /// </summary>
    public string? CodecKey { get; set; }
}

/// <summary>Entry point for JSON source generation.</summary>
[JsonSerializable(typeof(DispatcherConfig))]
[JsonSerializable(typeof(HttpServerRuntimeOptions))]
[JsonSerializable(typeof(HttpServerTlsConfig))]
[JsonSerializable(typeof(GrpcServerRuntimeConfig))]
[JsonSerializable(typeof(GrpcServerTlsConfig))]
[JsonSerializable(typeof(SslProtocols))]
[JsonSerializable(typeof(ClientCertificateMode))]
internal partial class DispatcherConfigJsonContext : JsonSerializerContext;

/// <summary>TLS config shape that is trimming/AOT safe (paths instead of certificate instances).</summary>
public sealed class HttpServerTlsConfig
{
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }
    public ClientCertificateMode ClientCertificateMode { get; set; } = ClientCertificateMode.NoCertificate;
    public bool? CheckCertificateRevocation { get; set; }
}

/// <summary>gRPC server TLS config shape (trim-safe).</summary>
public sealed class GrpcServerTlsConfig
{
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }
    public ClientCertificateMode ClientCertificateMode { get; set; } = ClientCertificateMode.NoCertificate;
    public bool? CheckCertificateRevocation { get; set; }
    public SslProtocols? EnabledProtocols { get; set; }
}

/// <summary>gRPC server runtime config shape with interceptor aliases.</summary>
public sealed class GrpcServerRuntimeConfig
{
    public bool EnableHttp3 { get; set; }
    public int? MaxReceiveMessageSize { get; set; }
    public int? MaxSendMessageSize { get; set; }
    public TimeSpan? KeepAlivePingDelay { get; set; }
    public TimeSpan? KeepAlivePingTimeout { get; set; }
    public bool? EnableDetailedErrors { get; set; }
    public TimeSpan? ServerStreamWriteTimeout { get; set; }
    public TimeSpan? DuplexWriteTimeout { get; set; }
    public int? ServerStreamMaxMessageBytes { get; set; }
    public int? DuplexMaxMessageBytes { get; set; }
    public Http3RuntimeOptions? Http3 { get; set; }
    public List<string> Interceptors { get; set; } = new();
}
