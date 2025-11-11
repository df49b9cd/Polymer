using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Tests.Support;
using OmniRelay.TestSupport;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.Tests.Transport.Http;

public class Http3LimitParityTests
{
    [Http3Fact(Timeout = 60_000)]
    public async Task HttpInbound_WithHttp3_ChunkedPayloadOverLimit_Returns429WithProtocolHeader()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-http3-limits");

        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"https://127.0.0.1:{port}/");

        var runtime = new HttpServerRuntimeOptions
        {
            EnableHttp3 = true,
            MaxInMemoryDecodeBytes = 64
        };
        var tls = new HttpServerTlsOptions { Certificate = certificate };

        var options = new DispatcherOptions("http3-limits");
        var inbound = new HttpInbound([baseAddress.ToString()], serverRuntimeOptions: runtime, serverTlsOptions: tls);
        options.AddLifecycle("http3-limits-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "http3-limits",
            "limited::echo",
            (request, _) => ValueTask.FromResult(Hugo.Go.Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        try
        {
            using var handler = CreateHttp3Handler();
            using var client = new HttpClient(handler) { BaseAddress = baseAddress };
            client.DefaultRequestVersion = HttpVersion.Version30;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "limited::echo");

            using var content = new SlowChunkedContent(chunkSize: 32, chunkCount: 4, delay: TimeSpan.FromMilliseconds(75));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var response = await client.PostAsync("/", content, ct);

            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Status, out var statusValues));
            Assert.Contains(nameof(OmniRelayStatusCode.ResourceExhausted), statusValues);
            Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Protocol, out var protocolHeaders));
            Assert.Contains("HTTP/3", protocolHeaders);

            var payload = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(payload);
            Assert.Equal("RESOURCE_EXHAUSTED", document.RootElement.GetProperty("status").GetString());
            Assert.Equal("request body exceeds in-memory decode limit", document.RootElement.GetProperty("message").GetString());
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(ct);
        }
    }

    private static SocketsHttpHandler CreateHttp3Handler() => new()
    {
        AllowAutoRedirect = false,
        EnableMultipleHttp3Connections = true,
        SslOptions =
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols =
                [
                    SslApplicationProtocol.Http3,
                    SslApplicationProtocol.Http2,
                    SslApplicationProtocol.Http11
                ]
            }
    };

    private sealed class SlowChunkedContent : HttpContent
    {
        private readonly byte[] _chunk;
        private readonly int _chunkCount;
        private readonly TimeSpan _delay;

        public SlowChunkedContent(int chunkSize, int chunkCount, TimeSpan delay)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkCount);

            _chunk = new byte[chunkSize];
            RandomNumberGenerator.Fill(_chunk);
            _chunkCount = chunkCount;
            _delay = delay;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            for (var i = 0; i < _chunkCount; i++)
            {
                try
                {
                    await stream.WriteAsync(_chunk.AsMemory(), default);
                    await stream.FlushAsync();
                }
                catch (IOException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (_delay > TimeSpan.Zero)
                {
                    await Task.Delay(_delay);
                }
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            SerializeToStreamAsync(stream, context);
    }

}
