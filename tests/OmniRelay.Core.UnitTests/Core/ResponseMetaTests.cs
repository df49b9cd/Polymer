using System;
using System.Collections.Generic;
using OmniRelay.Core;
using Xunit;

namespace OmniRelay.Core.UnitTests.Core;

public class ResponseMetaTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void WithHeader_AddsOrUpdates_Header_CaseInsensitive()
    {
        var meta = new ResponseMeta(encoding: "json");
        var updated = meta.WithHeader("X-Test", "1");
        Assert.True(updated.TryGetHeader("x-test", out var value));
        Assert.Equal("1", value);

        var updated2 = updated.WithHeader("x-TEST", "2");
        Assert.True(updated2.TryGetHeader("X-Test", out var v2));
        Assert.Equal("2", v2);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void WithHeaders_Merges_Existing()
    {
        var meta = new ResponseMeta(encoding: "json").WithHeader("a", "1");
        var merged = meta.WithHeaders([new KeyValuePair<string, string>("b", "2"), new KeyValuePair<string, string>("A", "3")]);
        Assert.True(merged.TryGetHeader("a", out var v1));
        Assert.Equal("3", v1);
        Assert.True(merged.TryGetHeader("b", out var v2));
        Assert.Equal("2", v2);
    }
}
