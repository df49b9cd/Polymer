using OmniRelay.Core.Transport;
using Xunit;

namespace OmniRelay.Core.UnitTests.Transport;

public class OnewayAckTests
{
    [Fact]
    public void Ack_Defaults_Meta_WhenNull()
    {
        var ack = OnewayAck.Ack();
        Assert.NotNull(ack.Meta);
    }

    [Fact]
    public void Ack_Uses_Provided_Meta()
    {
        var meta = new OmniRelay.Core.ResponseMeta(encoding: "json");
        var ack = OnewayAck.Ack(meta);
        Assert.Same(meta, ack.Meta);
    }
}
