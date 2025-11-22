namespace OmniRelay.ControlPlane.Bootstrap;

/// <summary>Tracks bootstrap token usage to prevent replay.</summary>
public interface IBootstrapReplayProtector
{
    bool TryConsume(Guid tokenId, DateTimeOffset expiresAt, int? maxUses);
}
