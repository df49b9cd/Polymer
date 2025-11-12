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
        updated.TryGetHeader("x-test", out var value).ShouldBeTrue();
        value.ShouldBe("1");

        var updated2 = updated.WithHeader("x-TEST", "2");
        updated2.TryGetHeader("X-Test", out var v2).ShouldBeTrue();
        v2.ShouldBe("2");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void WithHeaders_Merges_Existing()
    {
        var meta = new ResponseMeta(encoding: "json").WithHeader("a", "1");
        var merged = meta.WithHeaders([new KeyValuePair<string, string>("b", "2"), new KeyValuePair<string, string>("A", "3")]);
        merged.TryGetHeader("a", out var v1).ShouldBeTrue();
        v1.ShouldBe("3");
        merged.TryGetHeader("b", out var v2).ShouldBeTrue();
        v2.ShouldBe("2");
    }
}
