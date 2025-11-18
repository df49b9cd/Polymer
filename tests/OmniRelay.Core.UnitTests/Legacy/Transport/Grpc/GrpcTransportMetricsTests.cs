using OmniRelay.Core;
using OmniRelay.Transport.Grpc;
using Xunit;

namespace OmniRelay.Tests.Transport.Grpc;

public class GrpcTransportMetricsTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void CreateBaseTags_WithProtocol_AddsNetworkMetadata()
    {
        var meta = new RequestMeta(
            service: "inventory",
            procedure: "get-item",
            headers: [new KeyValuePair<string, string>("rpc.protocol", "HTTP/3")]);

        var tags = GrpcTransportMetrics.CreateBaseTags(meta);

        tags.ShouldContain(tag => tag.Key == "rpc.protocol" && string.Equals(tag.Value as string, "HTTP/3", StringComparison.Ordinal));
        tags.ShouldContain(tag => tag.Key == "network.protocol.name" && string.Equals(tag.Value as string, "http", StringComparison.Ordinal));
        tags.ShouldContain(tag => tag.Key == "network.protocol.version" && string.Equals(tag.Value as string, "3", StringComparison.Ordinal));
    }
}
