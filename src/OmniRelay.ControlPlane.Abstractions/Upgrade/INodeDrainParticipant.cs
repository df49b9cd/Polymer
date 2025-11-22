namespace OmniRelay.ControlPlane.Upgrade;

/// <summary>Represents a component that participates in node drain/upgrade workflows.</summary>
public interface INodeDrainParticipant
{
    /// <summary>Instructs the participant to reject new work and wait for in-flight tasks to complete.</summary>
    ValueTask DrainAsync(CancellationToken cancellationToken);

    /// <summary>Resumes normal operation after a drain completes.</summary>
    ValueTask ResumeAsync(CancellationToken cancellationToken);
}
