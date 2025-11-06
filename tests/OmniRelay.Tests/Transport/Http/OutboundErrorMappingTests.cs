using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.Tests.Transport.Http;

public class OutboundErrorMappingTests
{
    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(_response);
    }

    [Fact(Timeout = 30000)]
    public async Task JsonErrorBody_IsMappedToOmniRelayError()
    {
        var ct = TestContext.Current.CancellationToken;
        var json = "{\"message\":\"bad request\",\"status\":\"InvalidArgument\",\"code\":\"E_BAD\"}";
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(json)
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var client = new HttpClient(new StubHandler(response));
        var outbound = new HttpOutbound(client, new Uri("http://example/yarpc"));
        await outbound.StartAsync(ct);

        var meta = new RequestMeta(service: "svc", procedure: "proc::unary", transport: "http");
        var call = await ((IUnaryOutbound)outbound).CallAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), ct);
        Assert.True(call.IsFailure);
        var err = call.Error!;
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, OmniRelayErrorAdapter.ToStatus(err));
        Assert.Equal("bad request", err.Message);
        Assert.Equal("E_BAD", err.Code);
    }

    [Fact(Timeout = 30000)]
    public async Task NonJsonError_FallsBackToStatusMapping()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("Unavailable")
        };
        using var client = new HttpClient(new StubHandler(response));
        var outbound = new HttpOutbound(client, new Uri("http://example/yarpc"));
        await outbound.StartAsync(ct);

        var meta = new RequestMeta(service: "svc", procedure: "proc::unary", transport: "http");
        var call = await ((IUnaryOutbound)outbound).CallAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), ct);
        Assert.True(call.IsFailure);
        Assert.Equal(OmniRelayStatusCode.Unavailable, OmniRelayErrorAdapter.ToStatus(call.Error!));
    }
}
