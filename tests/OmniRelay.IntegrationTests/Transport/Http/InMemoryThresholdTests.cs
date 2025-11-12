using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Transport.Http;
using Xunit;
using static OmniRelay.IntegrationTests.Support.TransportTestHelper;

namespace OmniRelay.IntegrationTests.Transport.Http;

public sealed class InMemoryThresholdTests : TransportIntegrationTest
{
    public InMemoryThresholdTests(ITestOutputHelper output)
        : base(output)
    {
    }

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
        await using var host = await StartDispatcherAsync(nameof(ContentLengthAboveThreshold_Returns429), dispatcher, ct);
        await WaitForHttpEndpointReadyAsync(baseAddress, ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "big::payload");
        // Create payload larger than 64 bytes
        var big = new string('x', 128);
        using var content = new StringContent(big, Encoding.UTF8, "text/plain");
        using var resp = await httpClient.PostAsync("/", content, ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
    }

    [Fact(Timeout = 30000)]
    public async Task ChunkedAboveThreshold_Returns429()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("threshold");
        var runtime = new HttpServerRuntimeOptions { MaxInMemoryDecodeBytes = 64 };
        var inbound = new HttpInbound([baseAddress.ToString()], serverRuntimeOptions: runtime);
        options.AddLifecycle("http", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "threshold",
            "big::payload",
            (request, _) => ValueTask.FromResult(Hugo.Go.Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartDispatcherAsync(nameof(ChunkedAboveThreshold_Returns429), dispatcher, ct);
        await WaitForHttpEndpointReadyAsync(baseAddress, ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "big::payload");
        httpClient.DefaultRequestHeaders.TransferEncodingChunked = true;

        var payload = new string('y', 128);
        var stream = new NonSeekableReadStream(Encoding.UTF8.GetBytes(payload));
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        using var resp = await httpClient.PostAsync("/", content, ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
    }

    [Fact(Timeout = 30000)]
    public async Task ChunkedBelowThreshold_Succeeds()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("threshold");
        var runtime = new HttpServerRuntimeOptions { MaxInMemoryDecodeBytes = 64 };
        var inbound = new HttpInbound([baseAddress.ToString()], serverRuntimeOptions: runtime);
        options.AddLifecycle("http", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "threshold",
            "big::payload",
            (request, _) => ValueTask.FromResult(Hugo.Go.Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartDispatcherAsync(nameof(ChunkedBelowThreshold_Succeeds), dispatcher, ct);
        await WaitForHttpEndpointReadyAsync(baseAddress, ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "big::payload");
        httpClient.DefaultRequestHeaders.TransferEncodingChunked = true;

        var payload = new string('z', 16);
        var stream = new NonSeekableReadStream(Encoding.UTF8.GetBytes(payload));
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        using var resp = await httpClient.PostAsync("/", content, ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private sealed class NonSeekableReadStream(byte[] buffer) : Stream
    {
        private readonly ReadOnlyMemory<byte> _buffer = buffer;
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _buffer.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var toCopy = Math.Min(count, remaining);
            _buffer.Slice(_position, toCopy).Span.CopyTo(buffer.AsSpan(offset, toCopy));
            _position += toCopy;
            return toCopy;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var remaining = _buffer.Length - _position;
            if (remaining <= 0)
            {
                return ValueTask.FromResult(0);
            }

            var toCopy = Math.Min(buffer.Length, remaining);
            _buffer.Slice(_position, toCopy).Span.CopyTo(buffer.Span.Slice(0, toCopy));
            _position += toCopy;
            return ValueTask.FromResult(toCopy);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(Read(buffer, offset, count));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
