namespace OmniRelay.Diagnostics;

public sealed class NullPeerDiagnosticsProvider : IPeerDiagnosticsProvider
{
    public static NullPeerDiagnosticsProvider Instance { get; } = new();

    public PeerDiagnosticsResponse CreateSnapshot() =>
        new("v1", DateTimeOffset.UtcNow, string.Empty, Array.Empty<PeerDiagnosticsPeer>());

    public NullPeerDiagnosticsProvider()
    {
    }
}
