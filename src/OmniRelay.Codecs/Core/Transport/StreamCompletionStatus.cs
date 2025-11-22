namespace OmniRelay.Core.Transport;

/// <summary>
/// Indicates the terminal status of a streaming call.
/// </summary>
public enum StreamCompletionStatus
{
    /// <summary>No completion recorded.</summary>
    None = 0,
    /// <summary>The stream completed successfully.</summary>
    Succeeded = 1,
    /// <summary>The stream was cancelled.</summary>
    Cancelled = 2,
    /// <summary>The stream completed with an error.</summary>
    Faulted = 3
}
