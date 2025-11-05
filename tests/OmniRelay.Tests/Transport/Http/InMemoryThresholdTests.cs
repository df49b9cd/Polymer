using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.Tests.Transport.Http;

public class InMemoryThresholdTests
{
    [Fact(Timeout = 30000)]
    public async Task ContentLengthAboveThreshold_Returns429()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("threshold");
        var runtime = new HttpServerRuntimeOptions { MaxInMemoryDecodeBytes = 64 }; // 64 bytes
        var inbound = new HttpInbound([baseAddress.ToString()], serverRuntimeOptions: runtime);
        options.AddLifecycle("http", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "threshold",
            "big::payload",
            (request, _) => ValueTask.FromResult(Hugo.Go.Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/");
        req.Headers.Add(HttpTransportHeaders.Procedure, "big::payload");
        // Create payload larger than 64 bytes
        var big = new string('x', 128);
        req.Content = new StringContent(big, Encoding.UTF8, "text/plain");
        using var resp = await httpClient.SendAsync(req, ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);

        await dispatcher.StopAsync(ct);
    }
}
