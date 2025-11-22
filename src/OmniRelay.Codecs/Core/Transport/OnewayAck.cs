namespace OmniRelay.Core.Transport;

/// <summary>
/// Acknowledgement envelope for oneway requests.
/// </summary>
public sealed record OnewayAck(ResponseMeta Meta)
{
    /// <summary>
    /// Creates an acknowledgement with optional response metadata.
    /// </summary>
    public static OnewayAck Ack(ResponseMeta? meta = null) =>
        new(meta ?? new ResponseMeta());
}
