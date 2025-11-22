namespace OmniRelay.Diagnostics;

/// <summary>Represents a scheduled health probe.</summary>
public interface IHealthProbe
{
    string Name { get; }

    TimeSpan Interval { get; }

    ValueTask<ProbeResult> ExecuteAsync(CancellationToken cancellationToken);
}
