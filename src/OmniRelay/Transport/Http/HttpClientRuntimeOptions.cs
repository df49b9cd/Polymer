namespace OmniRelay.Transport.Http;

/// <summary>
/// Runtime options for HTTP clients controlling protocol negotiation and version policies.
/// Use to request HTTP/3 when available or pin a specific HTTP version per call.
/// </summary>
public sealed class HttpClientRuntimeOptions
{
    public bool EnableHttp3 { get; init; }

    public Version? RequestVersion { get; init; }

    public HttpVersionPolicy? VersionPolicy { get; init; }
}
