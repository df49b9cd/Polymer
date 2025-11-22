using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace OmniRelay.Core.Extensions;

internal sealed class NativeExtensionHost
{
    private readonly ExtensionFailurePolicy _policy;
    private readonly HashAlgorithmName _signatureAlgo;
    private readonly byte[] _publicKey;
    private readonly ExtensionTelemetryRecorder _telemetry;

    public NativeExtensionHost(ExtensionFailurePolicy policy, HashAlgorithmName signatureAlgo, byte[] publicKey, ILogger<NativeExtensionHost> logger)
    {
        _policy = policy;
        _signatureAlgo = signatureAlgo;
        _publicKey = publicKey ?? throw new ArgumentNullException(nameof(publicKey));
        _telemetry = new ExtensionTelemetryRecorder(logger);
    }

    public bool TryLoad(ExtensionPackage package)
    {
        if (package.Type != ExtensionType.Native)
        {
            _telemetry.RecordRejected(package, "wrong type");
            return false;
        }

        if (!package.IsSigned(_signatureAlgo, _publicKey))
        {
            _telemetry.RecordRejected(package, "invalid signature");
            return false;
        }

        try
        {
            // Placeholder: would write payload to temp and load via NativeLibrary.Load.
            _telemetry.RecordLoaded(package);
            return true;
        }
        catch (Exception ex)
        {
            _telemetry.RecordFailed(package, ex.Message);
            if (_policy.FailOpen)
            {
                return true;
            }
            return false;
        }
    }
}
