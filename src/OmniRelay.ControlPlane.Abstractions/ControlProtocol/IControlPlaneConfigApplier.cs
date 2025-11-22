namespace OmniRelay.ControlPlane.ControlProtocol;

public interface IControlPlaneConfigApplier
{
    Task ApplyAsync(string version, byte[] payload, CancellationToken cancellationToken = default);
}
