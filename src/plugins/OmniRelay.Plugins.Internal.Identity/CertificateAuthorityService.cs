using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Hugo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmniRelay.Protos.Ca;
using static Hugo.Go;

namespace OmniRelay.Identity;

/// <summary>In-process CA service for MeshKit agents (WORK-007): issues short-lived leaf certs and exposes the trust bundle.</summary>
public sealed partial class CertificateAuthorityService : CertificateAuthority.CertificateAuthorityBase, IDisposable
{
    private readonly CertificateAuthorityOptions _options;
    private readonly ILogger<CertificateAuthorityService> _logger;
    private readonly object _sync = new();
    private CaMaterial? _material;
    private DateTimeOffset _lastRootCheck = DateTimeOffset.MinValue;
    private bool _disposed;

    public CertificateAuthorityService(IOptions<CertificateAuthorityOptions> options, ILogger<CertificateAuthorityService> logger)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task<CertResponse> SubmitCsr(CsrRequest request, ServerCallContext context)
    {
        var result = await Task.Run(() => IssueAsync(request, context.CancellationToken), context.CancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw ToRpcException(result.Error!);
        }

        return result.Value;
    }

    public override Task<TrustBundleResponse> TrustBundle(TrustBundleRequest request, ServerCallContext context)
    {
        var material = GetMaterial();
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

        var material = GetMaterial();
        if (material.IsFailure)
        {
            return material.CastFailure<CertResponse>();
        }

        var csrInfo = ParseCsr(request);
        if (csrInfo.IsFailure)
        {
            return csrInfo.CastFailure<CertResponse>();
        }

        var binding = ValidateIdentityBinding(csrInfo.Value, request.NodeId, _options.RequireNodeBinding);
        if (binding.IsFailure)
        {
            return binding.CastFailure<CertResponse>();
        }

        var trustDomain = ValidateTrustDomain(csrInfo.Value);
        if (trustDomain.IsFailure)
        {
            return trustDomain.CastFailure<CertResponse>();
        }

        var issuedAt = DateTimeOffset.UtcNow;
        var notAfter = issuedAt + _options.LeafLifetime;

        var leafResult = IssueLeaf(material.Value.Root, csrInfo.Value, issuedAt, notAfter);
        if (leafResult.IsFailure)
        {
            return leafResult.CastFailure<CertResponse>();
        }

        var leaf = leafResult.Value;
        var chainBytes = Concat(leaf, material.Value.Root);
        var renewAfter = CalculateRenewAfter(issuedAt, notAfter, _options.RenewalWindow);

        var response = new CertResponse
        {
            Certificate = Google.Protobuf.ByteString.CopyFrom(leaf.Export(X509ContentType.Cert)),
            CertificateChain = Google.Protobuf.ByteString.CopyFrom(chainBytes),
            TrustBundle = Google.Protobuf.ByteString.CopyFrom(material.Value.TrustBundle),
            ExpiresAt = leaf.NotAfter.ToUniversalTime().ToString("O"),
            RenewAfter = renewAfter.ToUniversalTime().ToString("O"),
            IssuedAt = issuedAt.ToUniversalTime().ToString("O"),
            Subject = csrInfo.Value.CommonName ?? leaf.SubjectName.Name ?? string.Empty
        };
        response.SanDns.AddRange(csrInfo.Value.Sans.DnsNames);
        response.SanUri.AddRange(csrInfo.Value.Sans.Uris);
        if (response.SanDns.Count == 0 && !string.IsNullOrWhiteSpace(request.NodeId))
        {
            response.SanDns.Add(request.NodeId);
        }

        CaLog.Issued(_logger, request.NodeId, response.Subject ?? string.Empty, leaf.NotAfter);

        return Ok(response);
    }

    private Result<CaMaterial> GetMaterial()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return Err<CaMaterial>(Error.From("Certificate authority has been disposed.", "ca.disposed"));
            }

            if (_material is null || ShouldReloadRoot(_material))
            {
                var reload = CreateOrLoadRoot();
                if (reload.IsFailure)
                {
                    return reload;
                }

                _material?.Root.Dispose();

                _material = reload.Value;
                if (!string.IsNullOrWhiteSpace(_options.RootPfxPath))
                {
                    CaLog.RootReloaded(_logger, _options.RootPfxPath!);
                }
                else
                {
                    CaLog.RootCreated(_logger, _material.Root.Subject, _material.Root.NotAfter);
                }
            }

            return Ok(_material);
        }
    }

    private bool ShouldReloadRoot(CaMaterial current)
    {
        if (string.IsNullOrWhiteSpace(_options.RootPfxPath))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (_options.RootReloadInterval > TimeSpan.Zero && now - _lastRootCheck < _options.RootReloadInterval)
        {
            return false;
        }

        _lastRootCheck = now;
        if (!File.Exists(_options.RootPfxPath))
        {
            return false;
        }

        var lastWrite = File.GetLastWriteTimeUtc(_options.RootPfxPath);
        return lastWrite > current.LastWrite;
    }

    private Result<CaMaterial> CreateOrLoadRoot()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.RootPfxPath) && File.Exists(_options.RootPfxPath))
            {
                var persisted = X509CertificateLoader.LoadPkcs12FromFile(_options.RootPfxPath, _options.RootPfxPassword, X509KeyStorageFlags.Exportable);
                var persistedBundle = ExportPem(persisted);
                var lastWrite = File.GetLastWriteTimeUtc(_options.RootPfxPath);
                return Ok(new CaMaterial(persisted, persistedBundle, lastWrite));
            }

            using var rsa = RSA.Create(_options.KeySize);
            var dn = new X500DistinguishedName(_options.IssuerName);
            var req = new CertificateRequest(dn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-1);
            var notAfter = notBefore + _options.RootLifetime;

            var root = req.CreateSelfSigned(notBefore.UtcDateTime, notAfter.UtcDateTime);
            var bundle = ExportPem(root);

            if (!string.IsNullOrWhiteSpace(_options.RootPfxPath))
            {
                PersistRoot(root, _options.RootPfxPath!, _options.RootPfxPassword);
            }

            return Ok(new CaMaterial(root, bundle, DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            return Err<CaMaterial>(Error.FromException(ex));
        }
    }

    private static void PersistRoot(X509Certificate2 root, string path, string? password)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var pfx = root.Export(X509ContentType.Pkcs12, password);
        File.WriteAllBytes(path, pfx);
    }

    private static Result<CsrInfo> ParseCsr(CsrRequest request)
    {
        if (request?.Csr is null || request.Csr.Length == 0)
        {
            return Err<CsrInfo>(Error.From("csr is required", "ca.csr.required"));
        }

        try
        {
            var csr = CertificateRequest.LoadSigningRequest(request.Csr.ToByteArray(), HashAlgorithmName.SHA256);
            return Ok(new CsrInfo(csr, csr.SubjectName.Name, ExtractSans(csr)));
        }
        catch (Exception ex)
        {
            return Err<CsrInfo>(Error.FromException(ex).WithMetadata("ca.stage", "parse-csr"));
        }
    }

    private static SanInfo ExtractSans(CertificateRequest csr)
    {
        var dns = new List<string>();
        var uris = new List<string>();

        foreach (var ext in csr.CertificateExtensions)
        {
            if (ext.Oid?.Value != "2.5.29.17")
            {
                continue;
            }

            var reader = new AsnReader(ext.RawData, AsnEncodingRules.DER);
            var seq = reader.ReadSequence();
            while (seq.HasData)
            {
                var tag = seq.PeekTag();
                if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 2)))
                {
                    dns.Add(seq.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2)));
                }
                else if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 6)))
                {
                    uris.Add(seq.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 6)));
                }
                else
                {
                    seq.ReadEncodedValue();
                }
            }
        }

        return new SanInfo(uris, dns);
    }

    private static Result<Unit> ValidateIdentityBinding(CsrInfo csr, string nodeId, bool requireNodeBinding)
    {
        if (!requireNodeBinding)
        {
            return Ok(Unit.Value);
        }

        if (csr.Sans.DnsNames.Any(dns => string.Equals(dns, nodeId, StringComparison.OrdinalIgnoreCase)) ||
            MatchesCommonName(csr.CommonName, nodeId))
        {
            return Ok(Unit.Value);
        }

        return Err<Unit>(Error.From("CSR SAN/CN must include node_id", "ca.csr.node_binding"));
    }

    private static bool MatchesCommonName(string? subject, string nodeId)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return false;
        }

        if (subject.Contains(nodeId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var part in subject.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(part[3..], nodeId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private Result<Unit> ValidateTrustDomain(CsrInfo csr)
    {
        if (string.IsNullOrWhiteSpace(_options.TrustDomain))
        {
            return Ok(Unit.Value);
        }

        var expected = _options.TrustDomain!.TrimEnd('/');
        var uriSans = csr.Sans.Uris;
        if (uriSans.Count == 0)
        {
            return Ok(Unit.Value);
        }

        foreach (var uri in uriSans)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                return Err<Unit>(Error.From("CSR SAN uri invalid.", "ca.csr.trust_domain"));
            }

            var hostMatch = parsed.Host.Equals(expected, StringComparison.OrdinalIgnoreCase);
            var prefixMatch = parsed.AbsoluteUri.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
            if (!hostMatch && !prefixMatch)
            {
                return Err<Unit>(Error.From("CSR SAN trust domain mismatch.", "ca.csr.trust_domain"));
            }
        }

        return Ok(Unit.Value);
    }

    private static Result<X509Certificate2> IssueLeaf(
        X509Certificate2 issuer,
        CsrInfo csr,
        DateTimeOffset issuedAt,
        DateTimeOffset notAfter)
    {
        try
        {
            using var rsa = issuer.GetRSAPrivateKey() ?? throw new InvalidOperationException("Issuer RSA key required");
            var req = new CertificateRequest(csr.Request.SubjectName, csr.Request.PublicKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(CreateEnhancedKeyUsages(), false));
            var sanBuilder = new SubjectAlternativeNameBuilder();
            foreach (var dns in csr.Sans.DnsNames)
            {
                sanBuilder.AddDnsName(dns);
            }

            foreach (var uri in csr.Sans.Uris)
            {
                if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                {
                    sanBuilder.AddUri(parsed);
                }
            }

            req.CertificateExtensions.Add(sanBuilder.Build());

            var serial = GenerateSerialNumber();
            var leaf = req.Create(issuer, issuedAt.UtcDateTime, notAfter.UtcDateTime, serial);
            return Ok(leaf);
        }
        catch (Exception ex)
        {
            return Err<X509Certificate2>(Error.FromException(ex).WithMetadata("ca.stage", "issue-leaf"));
        }
    }

    private static DateTimeOffset CalculateRenewAfter(DateTimeOffset issuedAt, DateTimeOffset notAfter, double renewalWindow)
    {
        renewalWindow = Math.Clamp(renewalWindow, 0d, 1d);
        var lifetime = notAfter - issuedAt;
        var renewAfter = issuedAt + TimeSpan.FromTicks((long)(lifetime.Ticks * renewalWindow));
        return renewAfter > notAfter ? notAfter : renewAfter;
    }

    private static OidCollection CreateEnhancedKeyUsages()
    {
        return new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1"), // serverAuth
            new("1.3.6.1.5.5.7.3.2")  // clientAuth
        };
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
        return System.Text.Encoding.ASCII.GetBytes(writer.ToString());
    }

    private static byte[] GenerateSerialNumber()
    {
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static RpcException ToRpcException(Error error)
    {
        var status = new Status(StatusCode.InvalidArgument, error.Message ?? "error");
        return new RpcException(status);
    }

    private sealed record CaMaterial(X509Certificate2 Root, byte[] TrustBundle, DateTimeOffset LastWrite);

    private sealed record CsrInfo(CertificateRequest Request, string? CommonName, SanInfo Sans);

    private sealed record SanInfo(IReadOnlyList<string> Uris, IReadOnlyList<string> DnsNames);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_material is { Root: not null })
        {
            _material.Root.Dispose();
        }

        _disposed = true;
    }
}

internal static partial class CaLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "CA issued certificate for node {NodeId}, subject {Subject}, expires {NotAfter:o}")]
    public static partial void Issued(ILogger logger, string NodeId, string Subject, DateTimeOffset NotAfter);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "CA root reloaded from {Path}")]
    public static partial void RootReloaded(ILogger logger, string Path);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "CA root created with subject {Subject}, expires {NotAfter:o}")]
    public static partial void RootCreated(ILogger logger, string Subject, DateTimeOffset NotAfter);
}
