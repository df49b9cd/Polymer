using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Hugo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmniRelay.Protos.Ca;
using static Hugo.Go;

namespace OmniRelay.ControlPlane.Identity;

/// <summary>In-process CA service for MeshKit agents (WORK-007): issues short-lived leaf certs and exposes the trust bundle.</summary>
public sealed class CertificateAuthorityService : CertificateAuthority.CertificateAuthorityBase, IDisposable
{
    private readonly CertificateAuthorityOptions _options;
    private readonly ILogger<CertificateAuthorityService> _logger;
    private readonly Lazy<Result<CaMaterial>> _material;
    private bool _disposed;

    public CertificateAuthorityService(IOptions<CertificateAuthorityOptions> options, ILogger<CertificateAuthorityService> logger)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _material = new Lazy<Result<CaMaterial>>(CreateOrLoadRoot);
    }

    public override Task<CertResponse> SubmitCsr(CsrRequest request, ServerCallContext context)
    {
        var result = IssueAsync(request, context.CancellationToken);
        if (result.IsFailure)
        {
            throw ToRpcException(result.Error!);
        }

        return Task.FromResult(result.Value);
    }

    public override Task<TrustBundleResponse> TrustBundle(TrustBundleRequest request, ServerCallContext context)
    {
        var material = _material.Value;
        if (material.IsFailure)
        {
            throw ToRpcException(material.Error!);
        }

        return Task.FromResult(new TrustBundleResponse
        {
            TrustBundle = Google.Protobuf.ByteString.CopyFrom(material.Value.TrustBundle)
        });
    }

    private Result<CertResponse> IssueAsync(CsrRequest request, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return Err<CertResponse>(Error.From("Certificate authority has been disposed.", "ca.disposed"));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Err<CertResponse>(Error.Canceled("CSR request canceled", cancellationToken));
        }

        if (string.IsNullOrWhiteSpace(request.NodeId))
        {
            return Err<CertResponse>(Error.From("node_id is required", "ca.node_id.required"));
        }

        var material = _material.Value;
        if (material.IsFailure)
        {
            return material.CastFailure<CertResponse>();
        }

        var issueResult = IssueLeaf(material.Value.Root, request.NodeId);
        if (issueResult.IsFailure)
        {
            return issueResult.CastFailure<CertResponse>();
        }

        var leaf = issueResult.Value;
        var chainBytes = Concat(leaf, material.Value.Root);

        var response = new CertResponse
        {
            Certificate = Google.Protobuf.ByteString.CopyFrom(leaf.Export(X509ContentType.Cert)),
            CertificateChain = Google.Protobuf.ByteString.CopyFrom(chainBytes),
            TrustBundle = Google.Protobuf.ByteString.CopyFrom(material.Value.TrustBundle),
            ExpiresAt = leaf.NotAfter.ToUniversalTime().ToString("O")
        };

        return Ok(response);
    }

    private Result<CaMaterial> CreateOrLoadRoot()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.RootPfxPath) && File.Exists(_options.RootPfxPath))
            {
                var persisted = new X509Certificate2(_options.RootPfxPath, _options.RootPfxPassword, X509KeyStorageFlags.Exportable);
                var persistedBundle = ExportPem(persisted);
                return Ok(new CaMaterial(persisted, persistedBundle));
            }

            using var rsa = RSA.Create(3072);
            var dn = new X500DistinguishedName(_options.IssuerName);
            var req = new CertificateRequest(dn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

            var now = DateTimeOffset.UtcNow.AddMinutes(-5);
            var root = req.CreateSelfSigned(now, now.Add(_options.RootLifetime));

            if (!string.IsNullOrWhiteSpace(_options.RootPfxPath))
            {
                var pfx = root.Export(X509ContentType.Pfx, _options.RootPfxPassword);
                var directory = Path.GetDirectoryName(_options.RootPfxPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(_options.RootPfxPath!, pfx);
            }

            var trustBundle = ExportPem(root);
            return Ok(new CaMaterial(root, trustBundle));
        }
        catch (Exception ex)
        {
            return Err<CaMaterial>(Error.FromException(ex));
        }
    }

    private Result<X509Certificate2> IssueLeaf(X509Certificate2 issuer, string nodeId)
    {
        return Result.Try(() =>
        {
            using var rsa = RSA.Create(2048);
            var subject = new X500DistinguishedName($"CN={nodeId}");
            var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
            {
                new(Oids.ServerAuth),
                new(Oids.ClientAuth)
            }, false));

            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(nodeId);
            req.CertificateExtensions.Add(san.Build());

            var now = DateTimeOffset.UtcNow.AddMinutes(-1);
            var serial = RandomNumberGenerator.GetBytes(16);
            using var issuerKey = issuer.GetRSAPrivateKey() ?? throw new InvalidOperationException("CA certificate is missing a private key.");
            var generator = X509SignatureGenerator.CreateForRSA(issuerKey, RSASignaturePadding.Pkcs1);
            var cert = req.Create(issuer.SubjectName, generator, now, now.Add(_options.LeafLifetime), serial);
            return cert.CopyWithPrivateKey(rsa);
        });
    }

    private static byte[] Concat(params X509Certificate2[] certs)
    {
        using var ms = new MemoryStream();
        foreach (var cert in certs)
        {
            var raw = cert.Export(X509ContentType.Cert);
            ms.Write(raw, 0, raw.Length);
        }
        return ms.ToArray();
    }

    private static byte[] ExportPem(X509Certificate2 cert)
    {
        using var writer = new StringWriter();
        writer.WriteLine("-----BEGIN CERTIFICATE-----");
        writer.WriteLine(Convert.ToBase64String(cert.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks));
        writer.WriteLine("-----END CERTIFICATE-----");
        return System.Text.Encoding.UTF8.GetBytes(writer.ToString());
    }

    private static RpcException ToRpcException(Error error)
    {
        var metadata = new Metadata();
        if (!string.IsNullOrWhiteSpace(error.Code))
        {
            metadata.Add("error-code", error.Code);
        }

        if (error.Metadata is not null)
        {
            foreach (var pair in error.Metadata)
            {
                if (pair.Value is string value)
                {
                    metadata.Add(pair.Key, value);
                }
            }
        }

        var status = new Status(StatusCode.FailedPrecondition, error.Message ?? "certificate authority error");
        return new RpcException(status, metadata);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_material.IsValueCreated && _material.Value.IsSuccess)
        {
            _material.Value.Value.Root.Dispose();
        }
    }

    private sealed record CaMaterial(X509Certificate2 Root, byte[] TrustBundle);

    private static class Oids
    {
        public const string ServerAuth = "1.3.6.1.5.5.7.3.1";
        public const string ClientAuth = "1.3.6.1.5.5.7.3.2";
    }
}
