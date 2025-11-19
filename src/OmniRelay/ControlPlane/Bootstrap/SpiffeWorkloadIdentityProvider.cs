using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace OmniRelay.ControlPlane.Bootstrap;

public sealed class SpiffeWorkloadIdentityProvider : IWorkloadIdentityProvider, IDisposable
{
    private readonly SpiffeIdentityProviderOptions _options;
    private readonly ILogger<SpiffeWorkloadIdentityProvider> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly RandomNumberGenerator _rng = RandomNumberGenerator.Create();
    private bool _disposed;
    private static readonly Action<ILogger, string, string, Exception?> RevokedIdentityLog =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(1, nameof(SpiffeWorkloadIdentityProvider) + "_Revoked"),
            "Revoked SPIFFE identity {Identity}. Reason: {Reason}.");

    private static readonly Action<ILogger, string, string, string, TimeSpan, Exception?> IssuedIdentityLog =
        LoggerMessage.Define<string, string, string, TimeSpan>(
            LogLevel.Information,
            new EventId(2, nameof(SpiffeWorkloadIdentityProvider) + "_Issued"),
            "Issued SPIFFE identity {Identity} for cluster {ClusterId} role {Role} (lifetime {Lifetime}).");

    public SpiffeWorkloadIdentityProvider(
        SpiffeIdentityProviderOptions options,
        ILogger<SpiffeWorkloadIdentityProvider> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (!_options.SigningCertificate.HasPrivateKey)
        {
            throw new InvalidOperationException("SPIFFE signing certificate must include a private key.");
        }
    }

    public ValueTask<WorkloadCertificateBundle> IssueAsync(WorkloadIdentityRequest request, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        var bundle = IssueCertificate(request);
        return ValueTask.FromResult(bundle);
    }

    public ValueTask<WorkloadCertificateBundle> RenewAsync(WorkloadIdentityRequest request, CancellationToken cancellationToken = default) => IssueAsync(request, cancellationToken);

    public ValueTask RevokeAsync(string identity, string? reason = null, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        RevokedIdentityLog(_logger, identity ?? string.Empty, reason ?? string.Empty, null);
        return ValueTask.CompletedTask;
    }

    private WorkloadCertificateBundle IssueCertificate(WorkloadIdentityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = ResolveIdentity(request);
        using var key = RSA.Create(_options.KeySize);
        var subject = $"CN={request.Role}.{request.ClusterId}";
        var certificateRequest = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddUri(new Uri(identity));
        if (!string.IsNullOrWhiteSpace(request.NodeId))
        {
            sanBuilder.AddDnsName(request.NodeId!);
        }

        certificateRequest.CertificateExtensions.Add(sanBuilder.Build());
        certificateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(CreateEnhancedKeyUsages(), critical: false));
        certificateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));

        var now = _timeProvider.GetUtcNow();
        var notBefore = now.AddMinutes(-1).UtcDateTime;
        var lifetime = request.DesiredLifetime > TimeSpan.Zero ? request.DesiredLifetime : _options.DefaultLifetime;
        var maxLifetime = _options.MaximumLifetime;
        if (maxLifetime > TimeSpan.Zero && lifetime > maxLifetime)
        {
            lifetime = maxLifetime;
        }

        var notAfter = notBefore + lifetime;
        var serial = GenerateSerialNumber();
        using var issuedCertificate = certificateRequest.Create(_options.SigningCertificate, notBefore, notAfter, serial);
        var collection = new X509Certificate2Collection
        {
            issuedCertificate,
            _options.SigningCertificate
        };

        var password = string.IsNullOrEmpty(_options.CertificatePassword)
            ? BuildPassword()
            : _options.CertificatePassword!;

        var exported = collection.Export(X509ContentType.Pkcs12, password);
        if (exported is null || exported.Length == 0)
        {
            throw new InvalidOperationException("Failed to export SPIFFE identity certificate bundle.");
        }
        var metadata = MergeMetadata(request.Metadata);
        var renewAfter = now + TimeSpan.FromTicks((long)(lifetime.Ticks * _options.RenewalWindow));

        IssuedIdentityLog(_logger, identity, request.ClusterId, request.Role, lifetime, null);

        return new WorkloadCertificateBundle
        {
            Identity = identity,
            Provider = "spiffe",
            CertificateData = exported,
            CertificatePassword = password,
            TrustBundleData = _options.TrustBundle,
            Metadata = metadata,
            IssuedAt = now,
            RenewAfter = renewAfter,
            ExpiresAt = now + lifetime
        };
    }

    private string ResolveIdentity(WorkloadIdentityRequest request)
    {
        string template = request.IdentityHint ?? _options.IdentityTemplate ?? string.Empty;
        return template
            .Replace("{trustDomain}", _options.TrustDomain, StringComparison.OrdinalIgnoreCase)
            .Replace("{cluster}", request.ClusterId, StringComparison.OrdinalIgnoreCase)
            .Replace("{role}", request.Role, StringComparison.OrdinalIgnoreCase)
            .Replace("{nodeId}", request.NodeId ?? "unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static OidCollection CreateEnhancedKeyUsages()
    {
        var oids = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1"),
            new("1.3.6.1.5.5.7.3.2")
        };
        return oids;
    }

    private byte[] GenerateSerialNumber()
    {
        var bytes = new byte[16];
        _rng.GetBytes(bytes);
        return bytes;
    }

    private string BuildPassword()
    {
        var bytes = new byte[18];
        _rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    private ReadOnlyDictionary<string, string> MergeMetadata(IReadOnlyDictionary<string, string>? requestMetadata)
    {
        var merged = new Dictionary<string, string>(_options.MetadataComparer);
        foreach (var pair in _options.DefaultMetadata)
        {
            merged[pair.Key] = pair.Value;
        }

        if (requestMetadata is not null)
        {
            foreach (var pair in requestMetadata)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        return new ReadOnlyDictionary<string, string>(merged);
    }

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, nameof(SpiffeWorkloadIdentityProvider));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _rng.Dispose();
        _options.SigningCertificate.Dispose();
        _disposed = true;
    }
}

public sealed class SpiffeIdentityProviderOptions
{
    public required X509Certificate2 SigningCertificate { get; init; }

    public string TrustDomain { get; init; } = "omnirelay.mesh";

    public string IdentityTemplate { get; init; } = "spiffe://{trustDomain}/mesh/{cluster}/{role}/{nodeId}";

    public TimeSpan DefaultLifetime { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan MaximumLifetime { get; init; } = TimeSpan.FromHours(6);

    public double RenewalWindow { get; init; } = 0.8;

    public string? CertificatePassword { get; init; }

    public string? TrustBundle { get; init; }

    public IReadOnlyDictionary<string, string> DefaultMetadata { get; init; } = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public IEqualityComparer<string> MetadataComparer { get; init; } = StringComparer.OrdinalIgnoreCase;

    public int KeySize { get; init; } = 2048;
}
