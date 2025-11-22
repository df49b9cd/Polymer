using System.Collections.ObjectModel;

namespace OmniRelay.ControlPlane.Bootstrap;

/// <summary>Describes the data required to mint or renew a workload certificate.</summary>
public sealed class WorkloadIdentityRequest
{
    public string ClusterId { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public string NodeId { get; init; } = string.Empty;

    public string? Environment { get; init; }

    public IReadOnlyDictionary<string, string> Labels { get; init; } = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public BootstrapAttestationEvidence? Attestation { get; init; }

    public string? IdentityHint { get; init; }

    public TimeSpan DesiredLifetime { get; init; } = TimeSpan.FromMinutes(30);

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>Bundle containing the issued workload certificate, trust bundle, and metadata.</summary>
public sealed class WorkloadCertificateBundle
{
    public required string Identity { get; init; }

    public required string Provider { get; init; }

    public required byte[] CertificateData { get; init; }

    public string? CertificatePassword { get; init; }

    public string? TrustBundleData { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public DateTimeOffset IssuedAt { get; init; }

    public DateTimeOffset RenewAfter { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Provides workload identities and lifecycle management.</summary>
public interface IWorkloadIdentityProvider
{
    ValueTask<WorkloadCertificateBundle> IssueAsync(WorkloadIdentityRequest request, CancellationToken cancellationToken = default);

    ValueTask<WorkloadCertificateBundle> RenewAsync(WorkloadIdentityRequest request, CancellationToken cancellationToken = default);

    ValueTask RevokeAsync(string identity, string? reason = null, CancellationToken cancellationToken = default);
}
