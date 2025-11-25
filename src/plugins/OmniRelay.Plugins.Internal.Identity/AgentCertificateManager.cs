using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Hugo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmniRelay.Protos.Ca;
using static Hugo.Go;

namespace OmniRelay.Plugins.Internal.Identity;

/// <summary>
/// Issues and renews the local agent certificate by talking to the in-process CA.
/// Keeps the last issuance in memory and persists PFX + trust bundle to disk.
/// </summary>
public sealed class AgentCertificateManager : IDisposable
{
    private readonly ILogger<AgentCertificateManager> _logger;
    private readonly ICertificateAuthorityClient _client;
    private readonly MeshAgentOptions _options;
    private readonly AgentCertificateOptions _certOptions;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _nextRenewAfter = DateTimeOffset.MinValue;
    private DateTimeOffset _nextCheck = DateTimeOffset.MinValue;
    private CertificateBundle? _current;
    private bool _disposed;

    public AgentCertificateManager(
        ICertificateAuthorityClient client,
        IOptions<MeshAgentOptions> options,
        ILogger<AgentCertificateManager> logger,
        TimeProvider timeProvider)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _certOptions = _options.Certificates ?? throw new ArgumentException("Certificate options are required.", nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Ensures a current certificate is available, renewing when the configured window is reached.
    /// </summary>
    public async Task<Result<CertificateBundle>> EnsureCurrentAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return Err<CertificateBundle>(Error.From("Agent certificate manager has been disposed.", "agent.certs.disposed"));
        }

        if (!_certOptions.Enabled)
        {
            return Err<CertificateBundle>(Error.From("Agent certificate issuance is disabled.", "agent.certs.disabled"));
        }

        var now = _timeProvider.GetUtcNow();
        if (_current is not null && now < _nextRenewAfter && now < _nextCheck)
        {
            return Ok(_current);
        }

        if (now < _nextCheck)
        {
            return _current is not null
                ? Ok(_current)
                : Err<CertificateBundle>(Error.From("Certificate not yet issued.", "agent.certs.pending"));
        }

        _nextCheck = now + _certOptions.MinRenewalInterval;

        var issue = await IssueAsync(cancellationToken).ConfigureAwait(false);
        if (issue.IsFailure)
        {
            _nextCheck = now + _certOptions.FailureBackoff;
            return issue;
        }

        UpdateRenewalState(issue.Value);
        DisposeBundle(_current);
        _current = issue.Value;
        return issue;
    }

    private async Task<Result<CertificateBundle>> IssueAsync(CancellationToken cancellationToken)
    {
        try
        {
            var csrResult = BuildCsr(_options.NodeId, _certOptions);
            if (csrResult.IsFailure)
            {
                return csrResult.CastFailure<CertificateBundle>();
            }

            using var privateKey = csrResult.Value.Key;
            var response = await _client.SubmitCsrAsync(csrResult.Value.Request, cancellationToken).ConfigureAwait(false);

            var leaf = new X509Certificate2(response.Certificate.ToByteArray());
            var leafWithKey = leaf.CopyWithPrivateKey(privateKey);

            var chain = new X509Certificate2Collection { leafWithKey };
            if (response.CertificateChain?.Length > 0)
            {
                chain.Import(response.CertificateChain.ToByteArray());
            }

            PersistPfx(chain, _certOptions.PfxPath, _certOptions.PfxPassword);
            PersistTrustBundle(response.TrustBundle.ToByteArray(), _certOptions.TrustBundlePath);

            var renewAfter = ParseRenewAfter(response.RenewAfter, leafWithKey, _certOptions.RenewalWindow);
            return Ok(new CertificateBundle(leafWithKey, chain, renewAfter));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to issue agent certificate");
            return Err<CertificateBundle>(Error.FromException(ex).WithMetadata("agent.certs", "issue"));
        }
    }

    private static Result<(CsrRequest Request, RSA Key)> BuildCsr(string nodeId, AgentCertificateOptions options)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
        {
            return Err<(CsrRequest, RSA)>(Error.From("NodeId is required", "agent.certs.node_id"));
        }

        try
        {
            var rsa = RSA.Create(options.KeySize);
            var subject = new X500DistinguishedName($"CN={nodeId}");
            var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
            {
                new("1.3.6.1.5.5.7.3.1"), // serverAuth
                new("1.3.6.1.5.5.7.3.2")  // clientAuth
            }, false));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName(nodeId);
            foreach (var dns in options.SanDns)
            {
                sanBuilder.AddDnsName(dns);
            }

            foreach (var uri in options.SanUris)
            {
                if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                {
                    sanBuilder.AddUri(parsed);
                }
            }

            req.CertificateExtensions.Add(sanBuilder.Build());

            var csrBytes = req.CreateSigningRequest();
            var request = new CsrRequest
            {
                Csr = Google.Protobuf.ByteString.CopyFrom(csrBytes),
                NodeId = nodeId
            };

            return Ok((request, rsa));
        }
        catch (Exception ex)
        {
            return Err<(CsrRequest, RSA)>(Error.FromException(ex).WithMetadata("agent.certs", "csr"));
        }
    }

    private static void PersistPfx(X509Certificate2Collection chain, string path, string? password)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var exportable = new X509Certificate2Collection(chain);
        var pfx = exportable.Export(X509ContentType.Pkcs12, password);
        File.WriteAllBytes(path, pfx);
    }

    private static void PersistTrustBundle(byte[] trustBundle, string? path)
    {
        if (trustBundle is null || trustBundle.Length == 0 || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, trustBundle);
    }

    private void UpdateRenewalState(CertificateBundle bundle)
    {
        _nextRenewAfter = bundle.RenewAfter == DateTimeOffset.MinValue
            ? CalculateRenewAfter(bundle.Leaf, _certOptions.RenewalWindow)
            : bundle.RenewAfter;
    }

    private static DateTimeOffset ParseRenewAfter(string? renewAfter, X509Certificate2 leaf, double renewalWindow)
    {
        if (DateTimeOffset.TryParse(renewAfter, out var parsed))
        {
            return parsed;
        }

        return CalculateRenewAfter(leaf, renewalWindow);
    }

    private static DateTimeOffset CalculateRenewAfter(X509Certificate2 leaf, double renewalWindow)
    {
        renewalWindow = Math.Clamp(renewalWindow, 0d, 1d);
        var lifetime = leaf.NotAfter.ToUniversalTime() - leaf.NotBefore.ToUniversalTime();
        var window = TimeSpan.FromTicks((long)(lifetime.Ticks * renewalWindow));
        return leaf.NotBefore.ToUniversalTime() + window;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var result = await EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            _logger.LogWarning("agent certificate bootstrap failed: {Message}", result.Error?.Message ?? "unknown");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        DisposeBundle(_current);

        _disposed = true;
    }

    private static void DisposeBundle(CertificateBundle? bundle)
    {
        if (bundle is null)
        {
            return;
        }

        foreach (var cert in bundle.Chain)
        {
            cert.Dispose();
        }
    }
}

public sealed record CertificateBundle(X509Certificate2 Leaf, X509Certificate2Collection Chain, DateTimeOffset RenewAfter);
