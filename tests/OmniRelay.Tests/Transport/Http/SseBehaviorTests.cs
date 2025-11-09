using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.Tests.Transport.Http;

public class SseBehaviorTests
{
    [Fact(Timeout = 30000)]
    public async Task MissingAcceptHeader_ForSse_Returns406()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("sse");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http", httpInbound);
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.Register(new StreamProcedureSpec(
            "sse",
            "stream::events",
            (request, callOptions, ct) =>
            {
                var call = HttpStreamCall.CreateServerStream(request.Meta, new ResponseMeta(encoding: "text/plain"));
                return ValueTask.FromResult(Hugo.Go.Ok((IStreamCall)call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "stream::events");
        using var response = await httpClient.GetAsync("/", ct);

        Assert.Equal(HttpStatusCode.NotAcceptable, response.StatusCode);

        await dispatcher.StopOrThrowAsync(ct);
    }
}
