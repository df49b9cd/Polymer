using System.Net;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Transport.Http;
using Xunit;
using static OmniRelay.IntegrationTests.Support.TransportTestHelper;

namespace OmniRelay.IntegrationTests.Transport.Http;

public sealed class HeaderValidationTests(ITestOutputHelper output) : TransportIntegrationTest(output)
{
    [Fact(Timeout = 30000)]
    public async ValueTask MissingRpcProcedureHeader_Returns400()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("hdr");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http", httpInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "hdr",
            "noop",
            (request, _) => ValueTask.FromResult(Hugo.Go.Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartDispatcherAsync(nameof(MissingRpcProcedureHeader_Returns400), dispatcher, ct);
        await WaitForHttpEndpointReadyAsync(baseAddress, ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        using var content = new ByteArrayContent([]);
        using var response = await httpClient.PostAsync("/", content, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
