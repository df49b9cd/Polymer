using System;
using System.Collections.Generic;
using System.Reflection;
using Polymer.Core;
using Polymer.Transport.Grpc;
using Xunit;

namespace Polymer.Tests.Transport;

public class GrpcMetadataAdapterTests
{
    [Fact]
    public void CreateRequestMetadata_ThrowsForInvalidHeaderCharacters()
    {
        var meta = new RequestMeta(
            service: "svc",
            procedure: "echo",
            headers: new[]
            {
                new KeyValuePair<string, string>("custom", "bad\r\nvalue")
            });

        Assert.Throws<ArgumentException>(() => GrpcMetadataAdapter.CreateRequestMetadata(meta));
    }

    [Fact]
    public void ResolveDeadline_UsesSoonerOfDeadlineAndTtl()
    {
        var now = DateTime.UtcNow;
        var ttl = TimeSpan.FromSeconds(30);
        var deadline = now.AddSeconds(10);
        var meta = new RequestMeta(
            service: "svc",
            procedure: "echo",
            timeToLive: ttl,
            deadline: deadline);

        var resolved = InvokeResolveDeadline(meta);

        Assert.True(resolved.HasValue);
        Assert.InRange(resolved.Value, deadline.AddSeconds(-1), deadline.AddSeconds(1));
    }

    [Fact]
    public void ResolveDeadline_UsesTtlWhenDeadlineMissing()
    {
        var ttl = TimeSpan.FromMilliseconds(200);
        var meta = new RequestMeta(
            service: "svc",
            procedure: "echo",
            timeToLive: ttl);

        var before = DateTime.UtcNow;
        var resolved = InvokeResolveDeadline(meta);
        Assert.True(resolved.HasValue);
        Assert.InRange(resolved.Value, before.AddMilliseconds(50), before.AddMilliseconds(600));
    }

    [Fact]
    public void ResolveDeadline_ReturnsNullWhenNoDeadlineOrTtl()
    {
        var meta = new RequestMeta(service: "svc", procedure: "echo");
        var resolved = InvokeResolveDeadline(meta);
        Assert.Null(resolved);
    }

    private static DateTime? InvokeResolveDeadline(RequestMeta meta)
    {
        var method = typeof(GrpcOutbound).GetMethod(
            "ResolveDeadline",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var result = method.Invoke(null, new object[] { meta });
        return (DateTime?)result;
    }
}
