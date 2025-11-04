namespace YARPCore.Configuration;

public sealed class PolymerConfigurationException : Exception
{
    public PolymerConfigurationException(string message)
        : base(message)
    {
    }

    public PolymerConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
