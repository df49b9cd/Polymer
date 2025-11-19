namespace OmniRelay.Configuration.Models;

/// <summary>
/// Transport policy settings describing the allowed transports/encodings per endpoint category and explicit exceptions.
/// </summary>
public sealed class TransportPolicyConfiguration
{
    /// <summary>Gets the configured categories keyed by policy category name.</summary>
    public IDictionary<string, TransportPolicyCategoryConfiguration> Categories { get; init; } =
        CreateDefaultCategories();

    /// <summary>Gets the configured exceptions (overrides) for specific endpoints.</summary>
    public IList<TransportPolicyExceptionConfiguration> Exceptions { get; } =
        new List<TransportPolicyExceptionConfiguration>();

    private static Dictionary<string, TransportPolicyCategoryConfiguration> CreateDefaultCategories() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [TransportPolicyCategories.ControlPlane] = TransportPolicyCategoryConfiguration.CreateControlPlaneDefaults(),
            [TransportPolicyCategories.Diagnostics] = TransportPolicyCategoryConfiguration.CreateDiagnosticsDefaults()
        };
}

/// <summary>Defines allowed transports and encodings for a logical endpoint category.</summary>
public sealed class TransportPolicyCategoryConfiguration
{
    public string Category { get; set; } = TransportPolicyCategories.ControlPlane;

    public IList<string> AllowedTransports { get; } = new List<string>();

    public IList<string> AllowedEncodings { get; } = new List<string>();

    public string PreferredTransport { get; set; } = TransportPolicyTransports.Http3;

    public string PreferredEncoding { get; set; } = TransportPolicyEncodings.Protobuf;

    public bool RequirePreferredTransport { get; set; } = true;

    public bool RequirePreferredEncoding { get; set; } = true;

    public string? Description { get; set; }
        = "Default OmniRelay transport policy.";

    public static TransportPolicyCategoryConfiguration CreateControlPlaneDefaults() => new()
    {
        Category = TransportPolicyCategories.ControlPlane,
        AllowedTransports =
        {
            TransportPolicyTransports.Grpc
        },
        AllowedEncodings =
        {
            TransportPolicyEncodings.Protobuf
        },
        PreferredTransport = TransportPolicyTransports.Grpc,
        PreferredEncoding = TransportPolicyEncodings.Protobuf,
        RequirePreferredTransport = true,
        RequirePreferredEncoding = true,
        Description = "Mesh control-plane endpoints must use gRPC/Protobuf with HTTP/3 where available."
    };

    public static TransportPolicyCategoryConfiguration CreateDiagnosticsDefaults() => new()
    {
        Category = TransportPolicyCategories.Diagnostics,
        AllowedTransports =
        {
            TransportPolicyTransports.Http3
        },
        AllowedEncodings =
        {
            TransportPolicyEncodings.Json,
            TransportPolicyEncodings.Protobuf
        },
        PreferredTransport = TransportPolicyTransports.Http3,
        PreferredEncoding = TransportPolicyEncodings.Json,
        RequirePreferredTransport = true,
        RequirePreferredEncoding = false,
        Description = "Diagnostics endpoints must expose HTTP/3 first-class responses with JSON defaults."
    };
}

/// <summary>Defines an explicit override allowing a non-default transport/encoding combination.</summary>
public sealed class TransportPolicyExceptionConfiguration
{
    public string? Name { get; set; }

    public string Category { get; set; } = TransportPolicyCategories.Diagnostics;

    /// <summary>Specific endpoints (e.g., diagnostics:http) the exception applies to. Empty applies to entire category.</summary>
    public IList<string> AppliesTo { get; } = new List<string>();

    public IList<string> Transports { get; } = new List<string>();

    public IList<string> Encodings { get; } = new List<string>();

    public string? Reason { get; set; }

    public DateTimeOffset? ExpiresAfter { get; set; }

    public string? ApprovedBy { get; set; }
        = "transport-governance";
}

/// <summary>Known transport policy categories.</summary>
public static class TransportPolicyCategories
{
    public const string ControlPlane = "control-plane";
    public const string Diagnostics = "diagnostics";
}

/// <summary>Transport labels supported by the policy engine.</summary>
public static class TransportPolicyTransports
{
    public const string Http3 = "http3";
    public const string Http2 = "http2";
    public const string Grpc = "grpc";
}

/// <summary>Encoding labels understood by the policy engine.</summary>
public static class TransportPolicyEncodings
{
    public const string Protobuf = "protobuf";
    public const string Json = "json";
    public const string Raw = "raw";
}

/// <summary>Canonical endpoint identifiers evaluated by the policy engine.</summary>
public static class TransportPolicyEndpoints
{
    public const string DiagnosticsHttp = "diagnostics:http";
    public const string ControlPlaneGrpc = "control-plane:grpc";
}
