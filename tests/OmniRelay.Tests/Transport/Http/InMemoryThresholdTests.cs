using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
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
        await dispatcher.StartAsync(ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/");
        req.Headers.Add(HttpTransportHeaders.Procedure, "big::payload");
        req.Headers.TransferEncodingChunked = true;

        var payload = new string('y', 128);
        var stream = new NonSeekableReadStream(Encoding.UTF8.GetBytes(payload));
        req.Content = new StreamContent(stream);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        using var resp = await httpClient.SendAsync(req, ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);

        await dispatcher.StopAsync(ct);
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
        await dispatcher.StartAsync(ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        using var req = new HttpRequestMessage(HttpMethod.Post, "/");
        req.Headers.Add(HttpTransportHeaders.Procedure, "big::payload");
        req.Headers.TransferEncodingChunked = true;

        var payload = new string('z', 16);
        var stream = new NonSeekableReadStream(Encoding.UTF8.GetBytes(payload));
        req.Content = new StreamContent(stream);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        using var resp = await httpClient.SendAsync(req, ct);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await dispatcher.StopAsync(ct);
    }

    private sealed class NonSeekableReadStream : Stream
    {
        private readonly ReadOnlyMemory<byte> _buffer;
        private int _position;

        public NonSeekableReadStream(byte[] buffer)
        {
            _buffer = buffer;
        }

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
