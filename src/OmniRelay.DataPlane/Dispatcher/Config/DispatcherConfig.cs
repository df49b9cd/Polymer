using System.Text.Json.Serialization;

namespace OmniRelay.Dispatcher.Config;

/// <summary>Shipping-friendly, trim-safe configuration shape for dispatcher bootstrap.</summary>
public sealed class DispatcherConfig
{
    public string Service { get; set; } = "omnirelay";
    public InboundsConfig Inbounds { get; set; } = new();
    public OutboundsConfig Outbounds { get; set; } = new();
    public MiddlewareConfig Middleware { get; set; } = new();
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
}

public sealed class GrpcInboundConfig
{
    public string? Name { get; set; }
    public List<string> Urls { get; set; } = new();
    public bool? EnableDetailedErrors { get; set; }
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

/// <summary>Entry point for JSON source generation.</summary>
[JsonSerializable(typeof(DispatcherConfig))]
internal partial class DispatcherConfigJsonContext : JsonSerializerContext;
