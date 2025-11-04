using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using Xunit;
using YARPCore.Core;
using YARPCore.Core.Transport;
using YARPCore.Dispatcher;
using YARPCore.Transport.Http;

using static Hugo.Go;

namespace YARPCore.Tests.Transport;

public class HttpDuplexTransportTests
{
    [Fact]
    public async Task HttpDuplexOutbound_RawEncoding_SetsOctetStreamContentHeaders()
    {
        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var outbound = new HttpOutbound(httpClient, httpClient.BaseAddress!, disposeClient: true);

        await outbound.StartAsync(TestContext.Current.CancellationToken);

        var meta = new RequestMeta(
            service: "blob",
            procedure: "blob::echo",
            encoding: RawCodec.DefaultEncoding,
            transport: "http");

        var request = new Request<ReadOnlyMemory<byte>>(meta, new byte[] { 0x01, 0x02 });

        var result = await ((IUnaryOutbound)outbound).CallAsync(request, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(MediaTypeNames.Application.Octet, handler.LastRequest!.Content?.Headers.ContentType?.MediaType);
        Assert.True(handler.LastRequest.Headers.TryGetValues(HttpTransportHeaders.Encoding, out var values));
        Assert.Contains(RawCodec.DefaultEncoding, values);

        await outbound.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HttpInbound_RawEncoding_ReturnsOctetStreamContentType()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("blob");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http-inbound", httpInbound);

        var dispatcher = new YARPCore.Dispatcher.Dispatcher(options);

        dispatcher.Register(new UnaryProcedureSpec(
            "blob",
            "blob::echo",
            (request, cancellationToken) =>
            {
                var meta = new ResponseMeta(encoding: RawCodec.DefaultEncoding, transport: "http");
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(request.Body, meta)));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        try
        {
            using var httpClient = new HttpClient { BaseAddress = baseAddress };
            var payload = new byte[] { 0x0A, 0x0B, 0x0C };
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/");
            httpRequest.Headers.Add(HttpTransportHeaders.Procedure, "blob::echo");
            httpRequest.Headers.Add(HttpTransportHeaders.Encoding, RawCodec.DefaultEncoding);
            httpRequest.Content = new ByteArrayContent(payload);
            httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Octet);

            using var response = await httpClient.SendAsync(httpRequest, ct);

            Assert.True(response.IsSuccessStatusCode);
            Assert.Equal(MediaTypeNames.Application.Octet, response.Content.Headers.ContentType?.MediaType);
            Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Encoding, out var responseEncoding));
            Assert.Contains(RawCodec.DefaultEncoding, responseEncoding);
            var responsePayload = await response.Content.ReadAsByteArrayAsync(ct);
            Assert.Equal(payload, responsePayload);
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

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
