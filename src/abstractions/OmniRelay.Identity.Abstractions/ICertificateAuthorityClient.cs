using OmniRelay.Protos.Ca;

namespace OmniRelay.Identity;

/// <summary>Client abstraction for the in-process certificate authority (WORK-007).</summary>
public interface ICertificateAuthorityClient
{
    ValueTask<CertResponse> SubmitCsrAsync(CsrRequest request, CancellationToken cancellationToken = default);

    ValueTask<TrustBundleResponse> TrustBundleAsync(TrustBundleRequest request, CancellationToken cancellationToken = default);
}
