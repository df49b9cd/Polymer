namespace Polymer.Core.Transport;

public sealed record OnewayAck(ResponseMeta Meta)
{
    public static OnewayAck Ack(ResponseMeta? meta = null) =>
        new(meta ?? new ResponseMeta());
}
