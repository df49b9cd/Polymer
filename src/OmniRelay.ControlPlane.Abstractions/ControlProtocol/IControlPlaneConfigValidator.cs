namespace OmniRelay.ControlPlane.ControlProtocol;

public interface IControlPlaneConfigValidator
{
    bool Validate(byte[] payload, out string? errorMessage);
}
