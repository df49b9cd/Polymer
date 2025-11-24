using OmniRelay.Protos.Control;

namespace OmniRelay.ControlPlane.ControlProtocol;

/// <summary>Options that govern the control-plane watch protocol (capabilities, backoff hints).</summary>
public sealed class ControlProtocolOptions
{
    /// <summary>Capabilities supported by this control-plane instance. Requests advertising capabilities outside this set are rejected.</summary>
    public List<string> SupportedCapabilities { get; init; } = new() { "core/v1", "dsl/v1" };

    /// <summary>Default backoff hint emitted with successful responses.</summary>
    public TimeSpan DefaultBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Backoff hint when the client is missing capabilities.</summary>
    public TimeSpan UnsupportedCapabilityBackoff { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum buffered updates per subscriber before oldest entries are dropped.</summary>
    public int SubscriberBufferCapacity { get; init; } = 64;

    internal ControlBackoff ToDefaultBackoff() => new() { Millis = (long)DefaultBackoff.TotalMilliseconds };

    internal ControlBackoff ToUnsupportedBackoff() => new() { Millis = (long)UnsupportedCapabilityBackoff.TotalMilliseconds };
}
