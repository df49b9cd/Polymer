namespace OmniRelay.Configuration;

public sealed class OmniRelayConfigurationException : Exception
{
    public OmniRelayConfigurationException(string message)
        : base(message)
    {
    }

    public OmniRelayConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
