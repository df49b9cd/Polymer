using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.Tests.Transport.Http;

public class DuplexBadRequestTests
{
    [Fact(Timeout = 30000)]
    public async Task NonWebSocketGet_ForDuplex_Returns406()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("ws");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http", httpInbound);
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.Register(new DuplexProcedureSpec(
            "ws",
            "chat::echo",
            (request, ct) => ValueTask.FromResult(Hugo.Go.Err<IDuplexStreamCall>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unimplemented, "not implemented", transport: "http")))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "chat::echo");
        using var response = await httpClient.GetAsync("/", ct);
        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);

        await dispatcher.StopOrThrowAsync(ct);
    }
}
