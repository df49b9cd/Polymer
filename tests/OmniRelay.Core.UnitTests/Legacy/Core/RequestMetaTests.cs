using OmniRelay.Core;
using Xunit;

namespace OmniRelay.Tests.Core;

public class RequestMetaTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void WithHeader_UsesCaseInsensitiveDictionary()
    {
        var meta = new RequestMeta(service: "keyvalue")
            .WithHeader("Trace-Id", "abc123");

        meta.TryGetHeader("trace-id", out var value).ShouldBeTrue();
        value.ShouldBe("abc123");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void WithHeaders_MergesValues()
    {
        var meta = new RequestMeta(service: "keyvalue")
            .WithHeaders(
            [
                new KeyValuePair<string, string>("a", "1"),
                new KeyValuePair<string, string>("b", "2")
            ]);

        meta.TryGetHeader("A", out var a).ShouldBeTrue();
        a.ShouldBe("1");
        meta.TryGetHeader("b", out var b).ShouldBeTrue();
        b.ShouldBe("2");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Constructor_PopulatesProperties()
    {
        var deadline = DateTimeOffset.UtcNow;
        var meta = new RequestMeta(
            service: "billing",
            procedure: "list",
            caller: "frontend",
            encoding: "json",
            transport: "http",
            shardKey: "tenant-a",
            routingKey: "tenant-a",
            routingDelegate: "delegate",
            timeToLive: TimeSpan.FromSeconds(5),
            deadline: deadline);

        meta.Service.ShouldBe("billing");
        meta.Procedure.ShouldBe("list");
        meta.Caller.ShouldBe("frontend");
        meta.Encoding.ShouldBe("json");
        meta.Transport.ShouldBe("http");
        meta.ShardKey.ShouldBe("tenant-a");
        meta.RoutingKey.ShouldBe("tenant-a");
        meta.RoutingDelegate.ShouldBe("delegate");
        meta.TimeToLive.ShouldBe(TimeSpan.FromSeconds(5));
        meta.Deadline.ShouldBe(deadline);
    }
}
