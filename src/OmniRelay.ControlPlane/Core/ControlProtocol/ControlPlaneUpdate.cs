using Google.Protobuf;
using OmniRelay.Protos.Control;

namespace OmniRelay.ControlPlane.ControlProtocol;

/// <summary>Represents a control-plane update (snapshot or delta) ready for distribution to agents.</summary>
public sealed record ControlPlaneUpdate(
    string Version,
    long Epoch,
    ReadOnlyMemory<byte> Payload,
    IReadOnlyList<string> RequiredCapabilities,
    bool FullSnapshot,
    ReadOnlyMemory<byte> ResumeOpaque)
{
    internal static ControlPlaneUpdate Empty { get; } = new(
        "0",
        0,
        ReadOnlyMemory<byte>.Empty,
        Array.Empty<string>(),
        true,
        ReadOnlyMemory<byte>.Empty);

    internal WatchResumeToken ToResumeToken(string? nodeId = null)
    {
        var token = new WatchResumeToken
        {
            Version = Version,
            Epoch = Epoch
        };

        if (!ResumeOpaque.IsEmpty)
        {
            token.Opaque = ByteString.CopyFrom(ResumeOpaque.Span);
        }
        else if (!string.IsNullOrWhiteSpace(nodeId))
        {
            token.Opaque = ByteString.CopyFromUtf8(nodeId);
        }

        return token;
    }
}
