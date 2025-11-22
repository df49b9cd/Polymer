using OmniRelay.ControlPlane.ControlProtocol;

namespace OmniRelay.ControlPlane.Agent;

/// <summary>Accepts all payloads; replace with real schema validation.</summary>
public sealed class DefaultConfigValidator : IControlPlaneConfigValidator
{
    public bool Validate(byte[] payload, out string? errorMessage)
    {
        errorMessage = null;
        return true;
    }
}
