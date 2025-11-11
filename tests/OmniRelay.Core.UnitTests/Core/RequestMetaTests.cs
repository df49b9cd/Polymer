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

        Assert.Equal("svc", meta.Service);
        Assert.Equal("proc", meta.Procedure);
        Assert.Equal("me", meta.Caller);
        Assert.Equal("json", meta.Encoding);
        Assert.Equal("http", meta.Transport);
        Assert.Equal("s", meta.ShardKey);
        Assert.Equal("rk", meta.RoutingKey);
        Assert.Equal("rd", meta.RoutingDelegate);
        Assert.Equal(TimeSpan.FromSeconds(1), meta.TimeToLive);
        Assert.Equal(now, meta.Deadline);
        Assert.True(meta.TryGetHeader("H", out var hv));
        Assert.Equal("v", hv);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void WithHeader_And_WithHeaders_Merge_CaseInsensitive()
    {
        var meta = new RequestMeta(service: "svc");
        var updated = meta.WithHeader("X-One", "1");
        Assert.True(updated.TryGetHeader("x-one", out var v));
        Assert.Equal("1", v);

        var merged = updated.WithHeaders([new KeyValuePair<string, string>("x-ONE", "2"), new KeyValuePair<string, string>("Two", "2")]);
        Assert.True(merged.TryGetHeader("X-One", out var v1));
        Assert.Equal("2", v1);
        Assert.True(merged.TryGetHeader("two", out var v2));
        Assert.Equal("2", v2);
    }
}
