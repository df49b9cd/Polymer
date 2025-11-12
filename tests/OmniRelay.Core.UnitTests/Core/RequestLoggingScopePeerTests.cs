using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.Core;
using Xunit;

namespace OmniRelay.Core.UnitTests.Core;

public class RequestLoggingScopePeerTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void Create_AddsPeerFromActivityAndHeaders()
    {
        var meta = new RequestMeta(service: "svc", transport: "http")
            .WithHeader("rpc.protocol", "HTTP/3")
            .WithHeader("rpc.peer", "header-peer:8080");

        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("test");
        using var activity = source.StartActivity("test", ActivityKind.Client);
        activity?.SetTag("net.peer.name", "act-peer");
        activity?.SetTag("net.peer.port", 1234);

        var items = RequestLoggingScope.Create(meta, null, activity);
        items.ShouldContain(kv => kv.Key == "rpc.peer" && (string)kv.Value! == "header-peer:8080");
        items.ShouldContain(kv => kv.Key == "rpc.peer_port" && (int)kv.Value! == 1234);
        items.ShouldContain(kv => kv.Key == "network.protocol.name" && (string)kv.Value! == "http");
        items.ShouldContain(kv => kv.Key == "network.protocol.version" && (string)kv.Value! == "3");
        items.ShouldContain(kv => kv.Key == "network.transport" && (string)kv.Value! == "quic");
    }
}
