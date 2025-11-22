namespace OmniRelay.ControlPlane.Hosting;

/// <summary>Represents the lifecycle status of a managed component.</summary>
public enum LifecycleComponentStatus
{
    Created = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Stopped = 4,
    Faulted = 5
}
