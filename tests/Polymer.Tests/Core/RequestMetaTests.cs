using System;
using System.Collections.Generic;
using Polymer.Core;
using Xunit;

namespace Polymer.Tests.Core;

public class RequestMetaTests
{
    [Fact]
    public void WithHeader_UsesCaseInsensitiveDictionary()
    {
        var meta = new RequestMeta(service: "keyvalue")
            .WithHeader("Trace-Id", "abc123");

        Assert.True(meta.TryGetHeader("trace-id", out var value));
        Assert.Equal("abc123", value);
    }

    [Fact]
    public void WithHeaders_MergesValues()
    {
        var meta = new RequestMeta(service: "keyvalue")
            .WithHeaders(new[]
            {
                new KeyValuePair<string, string>("a", "1"),
                new KeyValuePair<string, string>("b", "2")
            });

        Assert.True(meta.TryGetHeader("A", out var a));
        Assert.Equal("1", a);
        Assert.True(meta.TryGetHeader("b", out var b));
        Assert.Equal("2", b);
    }

    [Fact]
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

        Assert.Equal("billing", meta.Service);
        Assert.Equal("list", meta.Procedure);
        Assert.Equal("frontend", meta.Caller);
        Assert.Equal("json", meta.Encoding);
        Assert.Equal("http", meta.Transport);
        Assert.Equal("tenant-a", meta.ShardKey);
        Assert.Equal("tenant-a", meta.RoutingKey);
        Assert.Equal("delegate", meta.RoutingDelegate);
        Assert.Equal(TimeSpan.FromSeconds(5), meta.TimeToLive);
        Assert.Equal(deadline, meta.Deadline);
    }
}
