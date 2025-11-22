using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace OmniRelay.Core.Extensions;

internal sealed class WasmExtensionHost
{
    private readonly WasmRuntime _runtime;
    private readonly ExtensionFailurePolicy _policy;
    private readonly HashAlgorithmName _signatureAlgo;
    private readonly byte[] _publicKey;
    private readonly ExtensionTelemetryRecorder _telemetry;

    public WasmExtensionHost(WasmRuntime runtime, ExtensionFailurePolicy policy, HashAlgorithmName signatureAlgo, byte[] publicKey, ILogger<WasmExtensionHost> logger)
    {
        _runtime = runtime;
        _policy = policy;
        _signatureAlgo = signatureAlgo;
        _publicKey = publicKey ?? throw new ArgumentNullException(nameof(publicKey));
        _telemetry = new ExtensionTelemetryRecorder(logger);
    }

    public bool TryLoad(ExtensionPackage package)
    {
        if (package.Type != ExtensionType.Wasm)
        {
            _telemetry.RecordRejected(package, "wrong type");
            return false;
        }

        if (!package.IsSigned(_signatureAlgo, _publicKey))
        {
            _telemetry.RecordRejected(package, "invalid signature");
            return false;
        }

        // Placeholder: instantiate runtime-specific engine; here we just record capability.
        _telemetry.RecordLoaded(package);
        return true;
    }
}
