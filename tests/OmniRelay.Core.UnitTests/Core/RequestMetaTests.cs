using System;
using System.Collections.Generic;
using OmniRelay.Core;
using Xunit;

namespace OmniRelay.Core.UnitTests.Core;

public class RequestMetaTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void Constructor_SetsProperties_And_Defaults()
    {
        var now = DateTimeOffset.UtcNow;
        var meta = new RequestMeta(
            service: "svc",
            procedure: "proc",
            caller: "me",
            encoding: "json",
            transport: "http",
            shardKey: "s",
            routingKey: "rk",
            routingDelegate: "rd",
            timeToLive: TimeSpan.FromSeconds(1),
            deadline: now,
            headers: [new KeyValuePair<string, string>("h", "v")]);

        meta.Service.ShouldBe("svc");
        meta.Procedure.ShouldBe("proc");
        meta.Caller.ShouldBe("me");
        meta.Encoding.ShouldBe("json");
        meta.Transport.ShouldBe("http");
        meta.ShardKey.ShouldBe("s");
        meta.RoutingKey.ShouldBe("rk");
        meta.RoutingDelegate.ShouldBe("rd");
        meta.TimeToLive.ShouldBe(TimeSpan.FromSeconds(1));
        meta.Deadline.ShouldBe(now);
        meta.TryGetHeader("H", out var hv).ShouldBeTrue();
        hv.ShouldBe("v");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void WithHeader_And_WithHeaders_Merge_CaseInsensitive()
    {
        var meta = new RequestMeta(service: "svc");
        var updated = meta.WithHeader("X-One", "1");
        updated.TryGetHeader("x-one", out var v).ShouldBeTrue();
        v.ShouldBe("1");

        var merged = updated.WithHeaders([new KeyValuePair<string, string>("x-ONE", "2"), new KeyValuePair<string, string>("Two", "2")]);
        merged.TryGetHeader("X-One", out var v1).ShouldBeTrue();
        v1.ShouldBe("2");
        merged.TryGetHeader("two", out var v2).ShouldBeTrue();
        v2.ShouldBe("2");
    }
}
