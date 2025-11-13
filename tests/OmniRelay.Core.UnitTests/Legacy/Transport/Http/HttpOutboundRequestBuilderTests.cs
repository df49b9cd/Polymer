using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.Tests.Transport.Http;

public sealed class HttpOutboundRequestBuilderTests
{
    [Fact]
    public void Constructor_WithHttp3EnabledOnHttpScheme_Throws()
    {
        using var client = new HttpClient(new HttpClientHandler());
        Should.Throw<InvalidOperationException>(() =>
            new HttpOutbound(
                client,
                new Uri("http://example.test/rpc"),
                disposeClient: false,
                runtimeOptions: new HttpClientRuntimeOptions { EnableHttp3 = true }));
    }

    [Fact(Timeout = 30_000)]
    public async Task CallAsync_MapsRequestMetadataToHttpRequest()
    {
        var captured = new CapturedRequest();
        using var handler = new RecordingHandler(async (request, token) =>
        {
            captured.Method = request.Method;
            captured.RequestUri = request.RequestUri;
            captured.Version = request.Version;
            captured.VersionPolicy = request.VersionPolicy;
            captured.Headers = request.Headers.ToDictionary(
                static header => header.Key,
                static header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
            captured.ContentHeaders = request.Content?.Headers.ToDictionary(
                static header => header.Key,
                static header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            captured.Payload = await request.Content!.ReadAsByteArrayAsync(token).ConfigureAwait(false);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("{\"message\":\"ok\"}"u8.ToArray())
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeNames.Application.Json);
            return response;
        });

        using var client = new HttpClient(handler);
        var requestUri = new Uri("https://example.test/rpc");
        var outbound = new HttpOutbound(
            client,
            requestUri,
            disposeClient: true,
            runtimeOptions: new HttpClientRuntimeOptions { EnableHttp3 = true });

        var ttl = TimeSpan.FromMilliseconds(1250);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        var meta = new RequestMeta(
            service: "svc",
            procedure: "svc::unary",
            caller: "caller-42",
            encoding: RawCodec.DefaultEncoding,
            transport: "http",
            shardKey: "shard-A",
            routingKey: "route-B",
            routingDelegate: "delegate-C",
            timeToLive: ttl,
            deadline: deadline,
            headers: [KeyValuePair.Create("X-Correlation-Id", "cor-123")]);
        var payload = "ping"u8.ToArray();
        var request = new Request<ReadOnlyMemory<byte>>(meta, payload);

        var ct = TestContext.Current.CancellationToken;
        await outbound.StartAsync(ct);
        try
        {
            var result = await ((IUnaryOutbound)outbound).CallAsync(request, ct);
            result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        }
        finally
        {
            await outbound.StopAsync(ct);
        }

        captured.Method.ShouldBe(HttpMethod.Post);
        captured.RequestUri.ShouldBe(requestUri);
        captured.Version.ShouldBe(HttpVersion.Version30);
        captured.VersionPolicy.ShouldBe(HttpVersionPolicy.RequestVersionOrLower);

        captured.Headers[HttpTransportHeaders.Transport].ShouldBe("http");
        captured.Headers[HttpTransportHeaders.Procedure].ShouldBe(meta.Procedure);
        captured.Headers[HttpTransportHeaders.Caller].ShouldBe(meta.Caller);
        captured.Headers[HttpTransportHeaders.Encoding].ShouldBe(meta.Encoding);
        captured.Headers[HttpTransportHeaders.ShardKey].ShouldBe(meta.ShardKey);
        captured.Headers[HttpTransportHeaders.RoutingKey].ShouldBe(meta.RoutingKey);
        captured.Headers[HttpTransportHeaders.RoutingDelegate].ShouldBe(meta.RoutingDelegate);
        captured.Headers[HttpTransportHeaders.TtlMs].ShouldBe(ttl.TotalMilliseconds.ToString(CultureInfo.InvariantCulture));

        var expectedDeadline = deadline.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        captured.Headers[HttpTransportHeaders.Deadline].ShouldBe(expectedDeadline);
        captured.Headers["X-Correlation-Id"].ShouldBe("cor-123");

        captured.ContentHeaders["Content-Type"].ShouldBe(MediaTypeNames.Application.Octet);
        captured.Payload.ShouldBe(payload);
    }

    private sealed class CapturedRequest
    {
        public HttpMethod? Method { get; set; }
        public Uri? RequestUri { get; set; }
        public Version? Version { get; set; }
        public HttpVersionPolicy VersionPolicy { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ContentHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Payload { get; set; } = [];
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder = responder ?? throw new ArgumentNullException(nameof(responder));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _responder(request, cancellationToken);
    }
}
