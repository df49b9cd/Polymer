namespace OmniRelay.Diagnostics;

/// <summary>Options controlling OmniRelay documentation endpoints.</summary>
public sealed class OmniRelayDocumentationOptions
{
    public bool EnableOpenApi { get; set; } = true;

    public bool EnableGrpcReflection { get; set; } = true;

    public string RoutePattern { get; set; } = "/openapi/omnirelay.json";

    public string? AuthorizationPolicy { get; set; }

    public IDictionary<string, string> Metadata { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
