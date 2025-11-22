namespace OmniRelay.Errors;

/// <summary>
/// Categorizes errors as client or server faults (or unknown).
/// </summary>
public enum OmniRelayFaultType
{
    Unknown = 0,
    Client = 1,
    Server = 2
}
