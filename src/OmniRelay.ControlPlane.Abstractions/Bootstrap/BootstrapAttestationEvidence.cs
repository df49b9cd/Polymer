namespace OmniRelay.ControlPlane.Bootstrap;

/// <summary>Evidence supplied by a node to prove workload identity.</summary>
public sealed class BootstrapAttestationEvidence
{
    public string? Provider { get; set; }

    /// <summary>Opaque attestation document (base64 or raw bytes).</summary>
    public byte[]? Evidence { get; set; }

    /// <summary>Structured attestation document (JWT/JSON) when available.</summary>
    public string? Document { get; set; }

    /// <summary>Optional signature associated with <see cref="Document"/>.</summary>
    public string? Signature { get; set; }

    /// <summary>Additional claims extracted from the attestation.</summary>
    public IDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
