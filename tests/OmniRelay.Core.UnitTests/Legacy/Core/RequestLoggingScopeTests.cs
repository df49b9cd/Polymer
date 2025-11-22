using OmniRelay.Core;
using Xunit;

namespace OmniRelay.Tests.Core;

public class RequestLoggingScopeTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void Create_IncludesProtocolMetadata()
    {
        var meta = new RequestMeta(
            service: "inventory",
            procedure: "get-item",
            headers: [new KeyValuePair<string, string>("rpc.protocol", "HTTP/3")]);

        var scopeItems = RequestLoggingScope.Create(meta);

        scopeItems.ShouldContain(pair => pair.Key == "rpc.protocol" && string.Equals(pair.Value as string, "HTTP/3", StringComparison.Ordinal));
        scopeItems.ShouldContain(pair => pair.Key == "network.protocol.name" && string.Equals(pair.Value as string, "http", StringComparison.Ordinal));
        scopeItems.ShouldContain(pair => pair.Key == "network.protocol.version" && string.Equals(pair.Value as string, "3", StringComparison.Ordinal));
    }
}
