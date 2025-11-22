using Microsoft.AspNetCore.Http;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.Tests.Transport.Http;

public sealed class HttpTransportMetricsTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void CreateBaseTags_WithHttp3_AddsNetworkMetadata()
    {
        var tags = HttpTransportMetrics.CreateBaseTags("svc", "svc::call", "POST", "HTTP/3");
        var lookup = tags.ToDictionary(static pair => pair.Key, static pair => pair.Value);

        lookup["rpc.system"].ShouldBe("http");
        lookup["rpc.service"].ShouldBe("svc");
        lookup["rpc.procedure"].ShouldBe("svc::call");
        lookup["http.request.method"].ShouldBe("POST");
        lookup["rpc.protocol"].ShouldBe("HTTP/3");
        lookup["network.protocol.name"].ShouldBe("http");
        lookup["network.protocol.version"].ShouldBe("3");
        lookup["network.transport"].ShouldBe("quic");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void AppendOutcome_WithStatusCode_AppendsOutcomeTags()
    {
        var baseTags = new[] { KeyValuePair.Create<string, object?>("rpc.system", "http") };
        var tagged = HttpTransportMetrics.AppendOutcome(baseTags, StatusCodes.Status503ServiceUnavailable, "error");
        var lookup = tagged.ToDictionary(static pair => pair.Key, static pair => pair.Value);

        lookup["rpc.system"].ShouldBe("http");
        lookup["http.response.status_code"].ShouldBe(StatusCodes.Status503ServiceUnavailable);
        lookup["outcome"].ShouldBe("error");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void AppendObservedProtocol_AppendsValueAndPreservesBaseTags()
    {
        var baseTags = new[] { KeyValuePair.Create<string, object?>("rpc.system", "http") };

        var unchanged = HttpTransportMetrics.AppendObservedProtocol(baseTags, "  ");
        unchanged.ShouldBeSameAs(baseTags);

        var enriched = HttpTransportMetrics.AppendObservedProtocol(baseTags, "HTTP/2");
        enriched.ShouldNotBeSameAs(baseTags);
        var lookup = enriched.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        lookup["rpc.system"].ShouldBe("http");
        lookup["http.observed_protocol"].ShouldBe("HTTP/2");
    }
}
