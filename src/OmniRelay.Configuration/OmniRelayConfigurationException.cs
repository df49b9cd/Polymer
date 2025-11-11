namespace OmniRelay.Configuration;

/// <summary>
/// Exception thrown when OmniRelay configuration is invalid or inconsistent.
/// </summary>
public sealed class OmniRelayConfigurationException : Exception
{
    /// <summary>Initializes a new instance with a message.</summary>
    public OmniRelayConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a message and inner exception.</summary>
    public OmniRelayConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public OmniRelayConfigurationException()
    {
    }
}
