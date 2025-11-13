using System.Net;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Transport.Http;
using Xunit;
using static OmniRelay.IntegrationTests.Support.TransportTestHelper;

namespace OmniRelay.IntegrationTests.Transport.Http;

public sealed class DuplexBadRequestTests(ITestOutputHelper output) : TransportIntegrationTest(output)
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
        await using var host = await StartDispatcherAsync(nameof(NonWebSocketGet_ForDuplex_Returns406), dispatcher, ct);
        await WaitForHttpEndpointReadyAsync(baseAddress, ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "chat::echo");
        using var response = await httpClient.GetAsync("/", ct);
        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);
    }
}
