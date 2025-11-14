namespace OmniRelay.ControlPlane.Upgrade;

/// <summary>Represents the upgrade/drain state for a node.</summary>
public enum NodeDrainState
{
    Active = 0,
    Draining = 1,
    Drained = 2,
    Failed = 3
}
