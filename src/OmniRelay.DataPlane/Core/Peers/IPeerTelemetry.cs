namespace OmniRelay.Core.Peers;

/// <summary>
/// Optional hook implemented by peers to receive lease outcome telemetry.
/// </summary>
public interface IPeerTelemetry
{
    /// <summary>Records the result of a lease including duration.</summary>
    void RecordLeaseResult(bool success, double durationMilliseconds);
}
