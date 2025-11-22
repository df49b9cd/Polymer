using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace OmniRelay.Core.Extensions;

internal sealed class DslExtensionHost
{
    private readonly ExtensionFailurePolicy _policy;
    private readonly HashAlgorithmName _signatureAlgo;
    private readonly byte[] _publicKey;
    private readonly ExtensionTelemetryRecorder _telemetry;

    public DslExtensionHost(ExtensionFailurePolicy policy, HashAlgorithmName signatureAlgo, byte[] publicKey, ILogger<DslExtensionHost> logger)
    {
        _policy = policy;
        _signatureAlgo = signatureAlgo;
        _publicKey = publicKey ?? throw new ArgumentNullException(nameof(publicKey));
        _telemetry = new ExtensionTelemetryRecorder(logger);
    }

    public bool TryLoad(ExtensionPackage package)
    {
        if (package.Type != ExtensionType.Dsl)
        {
            _telemetry.RecordRejected(package, "wrong type");
            return false;
        }

        if (!package.IsSigned(_signatureAlgo, _publicKey))
        {
            _telemetry.RecordRejected(package, "invalid signature");
            return false;
        }

        _telemetry.RecordLoaded(package);
        return true;
    }
}
