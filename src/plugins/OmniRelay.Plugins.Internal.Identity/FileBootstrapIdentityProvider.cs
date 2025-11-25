using System.Security.Cryptography.X509Certificates;

namespace OmniRelay.ControlPlane.Bootstrap;

/// <summary>Simple identity provider that serves a static certificate bundle from memory (used for tests/dev).</summary>
public sealed class FileBootstrapIdentityProvider : IWorkloadIdentityProvider
{
    private readonly byte[] _certificateData;
    private readonly string? _certificatePassword;
    private readonly string? _trustBundle;
    private readonly TimeProvider _timeProvider;

    public FileBootstrapIdentityProvider(byte[] certificateData, string? certificatePassword, string? trustBundle, TimeProvider? timeProvider = null)
    {
        _certificateData = certificateData ?? throw new ArgumentNullException(nameof(certificateData));
        _certificatePassword = certificatePassword;
        _trustBundle = trustBundle;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<WorkloadCertificateBundle> IssueAsync(WorkloadIdentityRequest request, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        return ValueTask.FromResult(new WorkloadCertificateBundle
        {
            Identity = request.IdentityHint ?? "file-bootstrap",
            Provider = "file",
            CertificateData = _certificateData,
            CertificatePassword = _certificatePassword,
            TrustBundleData = _trustBundle,
            IssuedAt = now,
            RenewAfter = now + TimeSpan.FromMinutes(15),
            ExpiresAt = now + TimeSpan.FromHours(1)
        });
    }

    public ValueTask<WorkloadCertificateBundle> RenewAsync(WorkloadIdentityRequest request, CancellationToken cancellationToken = default) => IssueAsync(request, cancellationToken);

    public ValueTask RevokeAsync(string identity, string? reason = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
