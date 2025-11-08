using System.Threading.Channels;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using Xunit;

namespace OmniRelay.Core.UnitTests.Transport;

public class ClientStreamRequestContextTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void Holds_Meta_And_Requests()
    {
        var meta = new RequestMeta(service: "svc");
        var ch = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        var ctx = new ClientStreamRequestContext(meta, ch.Reader);
        Assert.Same(meta, ctx.Meta);
        Assert.Same(ch.Reader, ctx.Requests);
    }
}
