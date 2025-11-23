using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Http;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Transport;

public class HttpDuplexTransportTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask HttpDuplexOutbound_RawEncoding_SetsOctetStreamContentHeaders()
    {
        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var outbound = HttpOutbound.Create(httpClient, httpClient.BaseAddress!, disposeClient: true).ValueOrChecked();

        await outbound.StartAsync(TestContext.Current.CancellationToken);

        var meta = new RequestMeta(
            service: "blob",
            procedure: "blob::echo",
            encoding: RawCodec.DefaultEncoding,
            transport: "http");

        var request = new Request<ReadOnlyMemory<byte>>(meta, new byte[] { 0x01, 0x02 });

        var result = await ((IUnaryOutbound)outbound).CallAsync(request, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        RecordingHandler.LastRequest.ShouldNotBeNull();
        RecordingHandler.LastRequest!.Content?.Headers.ContentType?.MediaType.ShouldBe(MediaTypeNames.Application.Octet);
        RecordingHandler.LastRequest.Headers.TryGetValues(HttpTransportHeaders.Encoding, out var values).ShouldBeTrue();
        values.ShouldNotBeNull();
        values!.ShouldContain(RawCodec.DefaultEncoding);

        await outbound.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask HttpInbound_RawEncoding_ReturnsOctetStreamContentType()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("blob");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http-inbound", httpInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.Register(new UnaryProcedureSpec(
            "blob",
            "blob::echo",
            (request, cancellationToken) =>
            {
                var meta = new ResponseMeta(encoding: RawCodec.DefaultEncoding, transport: "http");
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(request.Body, meta)));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsyncChecked(ct);

        try
        {
            using var httpClient = new HttpClient { BaseAddress = baseAddress };
            httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "blob::echo");
            httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Encoding, RawCodec.DefaultEncoding);

            var payload = new byte[] { 0x0A, 0x0B, 0x0C };
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Octet);

            using var response = await httpClient.PostAsync("/", content, ct);

            response.IsSuccessStatusCode.ShouldBeTrue();
            response.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypeNames.Application.Octet);
            response.Headers.TryGetValues(HttpTransportHeaders.Encoding, out var responseEncoding).ShouldBeTrue();
            responseEncoding.ShouldNotBeNull();
            responseEncoding!.ShouldContain(RawCodec.DefaultEncoding);
            var responsePayload = await response.Content.ReadAsByteArrayAsync(ct);
            responsePayload.ShouldBe(payload);
        }
        finally
        {
            await dispatcher.StopAsyncChecked(ct);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public static HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            };

            response.Headers.Add(HttpTransportHeaders.Transport, "http");
            response.Headers.Add(HttpTransportHeaders.Encoding, RawCodec.DefaultEncoding);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Octet);

            return Task.FromResult(response);
        }
    }
}
