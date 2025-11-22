namespace OmniRelay.ControlPlane.Bootstrap;

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
        var bundle = CreateBundle(request);
        return ValueTask.FromResult(bundle);
    }

    public ValueTask<WorkloadCertificateBundle> RenewAsync(WorkloadIdentityRequest request, CancellationToken cancellationToken = default) => IssueAsync(request, cancellationToken);

    public ValueTask RevokeAsync(string identity, string? reason = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    private WorkloadCertificateBundle CreateBundle(WorkloadIdentityRequest request)
    {
        var now = _timeProvider.GetUtcNow();
        var nodeId = string.IsNullOrWhiteSpace(request.NodeId) ? Guid.NewGuid().ToString("N") : request.NodeId;
        var identity = request.IdentityHint ?? $"file:{nodeId}";
        return new WorkloadCertificateBundle
        {
            Identity = identity,
            Provider = "file",
            CertificateData = (byte[])_certificateData.Clone(),
            CertificatePassword = _certificatePassword,
            TrustBundleData = _trustBundle,
            Metadata = request.Metadata,
            IssuedAt = now,
            RenewAfter = now,
            ExpiresAt = now
        };
    }
}
