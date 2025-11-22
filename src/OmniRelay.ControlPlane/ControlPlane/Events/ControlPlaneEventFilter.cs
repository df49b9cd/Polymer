namespace OmniRelay.ControlPlane.Events;

/// <summary>Optional filter applied per subscriber when delivering events.</summary>
public sealed record ControlPlaneEventFilter
{
    public string? ClusterId { get; init; }

    public string? Role { get; init; }

    public string? Region { get; init; }

    public string? EventType { get; init; }

    public string? Scope { get; init; }

    internal bool Matches(ControlPlaneEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (ClusterId is { Length: > 0 } cluster &&
            !string.Equals(cluster, evt.ClusterId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Role is { Length: > 0 } role &&
            !string.Equals(role, evt.Role, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Region is { Length: > 0 } region &&
            !string.Equals(region, evt.Region, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (EventType is { Length: > 0 } type &&
            !string.Equals(type, evt.EventType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Scope is { Length: > 0 } scope &&
            !string.Equals(scope, evt.Scope, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
