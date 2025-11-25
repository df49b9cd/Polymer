using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using OmniRelay.Protos.Ca;

namespace OmniRelay.Identity;

/// <summary>Lightweight client for the in-process certificate authority.</summary>
public sealed class CertificateAuthorityClient : ICertificateAuthorityClient
{
    private readonly CertificateAuthority.CertificateAuthorityClient _client;
    private readonly ILogger<CertificateAuthorityClient> _logger;

    public CertificateAuthorityClient(CertificateAuthority.CertificateAuthorityClient client, ILogger<CertificateAuthorityClient> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TrustBundleResponse> TrustBundleAsync(TrustBundleRequest request, CancellationToken cancellationToken = default)
    {
        return await _client.TrustBundleAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<CertResponse> SubmitCsrAsync(CsrRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.SubmitCsrAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CertificateAuthorityClient.SubmitCsr failed");
            throw;
        }
    }

    public async Task<TrustBundleResponse> GetTrustBundleAsync(CancellationToken cancellationToken)
    {
        return await _client.TrustBundleAsync(new TrustBundleRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static CertificateAuthorityClient Create(string address, ILogger<CertificateAuthorityClient> logger)
    {
        var channel = GrpcChannel.ForAddress(address);
        return new CertificateAuthorityClient(new CertificateAuthority.CertificateAuthorityClient(channel), logger);
    }
}
