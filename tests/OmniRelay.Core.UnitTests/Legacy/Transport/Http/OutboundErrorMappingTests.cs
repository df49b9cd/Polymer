using System.Net;
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
    public async ValueTask JsonErrorBody_IsMappedToOmniRelayError()
    {
        var ct = TestContext.Current.CancellationToken;
        var json = "{\"message\":\"bad request\",\"status\":\"InvalidArgument\",\"code\":\"E_BAD\"}";
        var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(json)
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var client = new HttpClient(new StubHandler(response));
        var outbound = HttpOutbound.Create(client, new Uri("http://example/yarpc")).ValueOrChecked();
        await outbound.StartAsync(ct);

        var meta = new RequestMeta(service: "svc", procedure: "proc::unary", transport: "http");
        var call = await ((IUnaryOutbound)outbound).CallAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), ct);
        call.IsFailure.ShouldBeTrue();
        var err = call.Error!;
        OmniRelayErrorAdapter.ToStatus(err).ShouldBe(OmniRelayStatusCode.InvalidArgument);
        err.Message.ShouldBe("bad request");
        err.Code.ShouldBe("E_BAD");
    }

    [Fact(Timeout = 30000)]
    public async ValueTask NonJsonError_FallsBackToStatusMapping()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("Unavailable")
        };
        using var client = new HttpClient(new StubHandler(response));
        var outbound = HttpOutbound.Create(client, new Uri("http://example/yarpc")).ValueOrChecked();
        await outbound.StartAsync(ct);

        var meta = new RequestMeta(service: "svc", procedure: "proc::unary", transport: "http");
        var call = await ((IUnaryOutbound)outbound).CallAsync(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), ct);
        call.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(call.Error!).ShouldBe(OmniRelayStatusCode.Unavailable);
    }
}
