using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Core;

public class ProtobufCallAdaptersTests
{
    private static ProtobufCodec<StringValue, StringValue> CreateCodec() => new();

    private static RequestMeta CreateRequestMeta(ProtobufCodec<StringValue, StringValue> codec) =>
        new(service: "svc", procedure: "rpc", transport: "grpc", encoding: codec.Encoding);

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task UnaryHandler_SuccessfullyEncodesResponse()
    {
        var codec = CreateCodec();
        var meta = CreateRequestMeta(codec);
        var requestPayload = codec.EncodeRequest(new StringValue { Value = "ping" }, meta).Value;
        var request = new Request<ReadOnlyMemory<byte>>(meta, requestPayload);

        var handler = ProtobufCallAdapters.CreateUnaryHandler<StringValue, StringValue>(
            codec,
            (typedRequest, ct) =>
            {
                var body = new StringValue { Value = typedRequest.Body.Value.ToUpperInvariant() };
                var responseMeta = new ResponseMeta();
                return ValueTask.FromResult(Response<StringValue>.Create(body, responseMeta));
            });

        var result = await handler(request, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var response = result.Value;
        Assert.Equal(codec.Encoding, response.Meta.Encoding);
        var decoded = codec.DecodeResponse(response.Body, response.Meta);
        Assert.True(decoded.IsSuccess);
        Assert.Equal("PING", decoded.Value.Value);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task UnaryHandler_ReturnsDecodeErrorWithoutInvokingHandler()
    {
        var codec = CreateCodec();
        var meta = CreateRequestMeta(codec);
        var request = new Request<ReadOnlyMemory<byte>>(meta, new byte[] { 1, 2, 3 });
        var invoked = false;

        var handler = ProtobufCallAdapters.CreateUnaryHandler<StringValue, StringValue>(
            codec,
            (_, _) =>
            {
                invoked = true;
                return ValueTask.FromResult(Response<StringValue>.Create(new StringValue(), new ResponseMeta()));
            });

        var result = await handler(request, TestContext.Current.CancellationToken);

        Assert.False(invoked);
        Assert.True(result.IsFailure);
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, OmniRelayErrorAdapter.ToStatus(result.Error!));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task UnaryHandler_HandlerExceptionReturnsInternalError()
    {
        var codec = CreateCodec();
        var meta = CreateRequestMeta(codec);
        var payload = codec.EncodeRequest(new StringValue { Value = "err" }, meta).Value;
        var request = new Request<ReadOnlyMemory<byte>>(meta, payload);

        var handler = ProtobufCallAdapters.CreateUnaryHandler<StringValue, StringValue>(
            codec,
            (_, _) => throw new InvalidOperationException("boom"));

        var result = await handler(request, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(OmniRelayStatusCode.Internal, OmniRelayErrorAdapter.ToStatus(result.Error!));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ServerStreamHandler_WritesMessagesAndPropagatesMetadata()
    {
        var codec = CreateCodec();
        var meta = CreateRequestMeta(codec);
        var payload = codec.EncodeRequest(new StringValue { Value = "hello" }, meta).Value;
        var request = new Request<ReadOnlyMemory<byte>>(meta, payload);
        var options = new StreamCallOptions(StreamDirection.Server);

        var handler = ProtobufCallAdapters.CreateServerStreamHandler<StringValue, StringValue>(
            codec,
            async (typedRequest, writer, ct) =>
            {
                Assert.Equal("hello", typedRequest.Body.Value);
                writer.ResponseMeta = new ResponseMeta(transport: "custom");
                await writer.WriteAsync(new StringValue { Value = "world" }, ct);
            });

        var result = await handler(request, options, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);

        var call = Assert.IsAssignableFrom<IStreamCall>(result.Value);
        var received = await call.Responses.ReadAsync(TestContext.Current.CancellationToken);
        var decoded = codec.DecodeResponse(received, call.ResponseMeta);
        Assert.True(decoded.IsSuccess);
        Assert.Equal("world", decoded.Value.Value);
        Assert.Equal(codec.Encoding, call.ResponseMeta.Encoding);
        Assert.Equal("custom", call.ResponseMeta.Transport);
        Assert.False(await call.Responses.WaitToReadAsync(TestContext.Current.CancellationToken));
        await call.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ServerStreamHandler_HandlerExceptionCompletesWithError()
    {
        var codec = CreateCodec();
        var meta = CreateRequestMeta(codec);
        var payload = codec.EncodeRequest(new StringValue { Value = "oops" }, meta).Value;
        var request = new Request<ReadOnlyMemory<byte>>(meta, payload);
        var options = new StreamCallOptions(StreamDirection.Server);

        var handler = ProtobufCallAdapters.CreateServerStreamHandler<StringValue, StringValue>(
            codec,
            (_, _, _) => throw new InvalidOperationException("fail"));

        var result = await handler(request, options, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        var call = Assert.IsAssignableFrom<IStreamCall>(result.Value);

        var closed = await Assert.ThrowsAsync<ChannelClosedException>(() => call.Responses.ReadAsync(TestContext.Current.CancellationToken).AsTask());
        var ex = Assert.IsType<OmniRelayException>(closed.InnerException);
        Assert.Equal(OmniRelayStatusCode.Internal, ex.StatusCode);
        await call.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ServerStreamWriter_WriteAsyncEncodingFailureCompletesWithError()
    {
        var codec = CreateCodec();
        var meta = CreateRequestMeta(codec);
        var payload = codec.EncodeRequest(new StringValue { Value = "a" }, meta).Value;
        var request = new Request<ReadOnlyMemory<byte>>(meta, payload);
        var options = new StreamCallOptions(StreamDirection.Server);

        var handler = ProtobufCallAdapters.CreateServerStreamHandler<StringValue, StringValue>(
            codec,
            async (_, writer, ct) =>
            {
                writer.ResponseMeta = new ResponseMeta(encoding: "unsupported");
                await Assert.ThrowsAsync<OmniRelayException>(async () => await writer.WriteAsync(new StringValue { Value = "b" }, ct));
            });

        var result = await handler(request, options, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        var call = Assert.IsAssignableFrom<IStreamCall>(result.Value);

        var closed = await Assert.ThrowsAsync<ChannelClosedException>(() => call.Responses.ReadAsync(TestContext.Current.CancellationToken).AsTask());
        var ex = Assert.IsType<OmniRelayException>(closed.InnerException);
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, ex.StatusCode);
        await call.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ClientStreamContext_ReadAllAsync_DecodesMessages()
    {
        var codec = CreateCodec();
        var meta = CreateRequestMeta(codec);
        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        var context = new ClientStreamRequestContext(meta, channel.Reader);
        var typedContext = new ProtobufCallAdapters.ProtobufClientStreamContext<StringValue, StringValue>(codec, context);

        var ct = TestContext.Current.CancellationToken;
        await channel.Writer.WriteAsync(codec.EncodeRequest(new StringValue { Value = "one" }, meta).Value, ct);
        await channel.Writer.WriteAsync(codec.EncodeRequest(new StringValue { Value = "two" }, meta).Value, ct);
        channel.Writer.TryComplete();

        var messages = new List<string>();
        await foreach (var message in typedContext.ReadAllAsync(ct))
        {
            messages.Add(message.Value);
        }

        Assert.Equal(new[] { "one", "two" }, messages);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ClientStreamContext_ReadAllAsync_InvalidPayloadThrows()
    {
        var codec = CreateCodec();
        var meta = CreateRequestMeta(codec);
        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        var context = new ClientStreamRequestContext(meta, channel.Reader);
        var typedContext = new ProtobufCallAdapters.ProtobufClientStreamContext<StringValue, StringValue>(codec, context);

        await channel.Writer.WriteAsync(new byte[] { 1, 2 }, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        var ex = await Assert.ThrowsAsync<OmniRelayException>(async () =>
        {
            await foreach (var _ in typedContext.ReadAllAsync(TestContext.Current.CancellationToken))
            {
            }
        });

        Assert.Equal(OmniRelayStatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ClientStreamHandler_EncodesResponse()
    {
        var codec = CreateCodec();
        var meta = CreateRequestMeta(codec);
        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        var context = new ClientStreamRequestContext(meta, channel.Reader);

        var handler = ProtobufCallAdapters.CreateClientStreamHandler<StringValue, StringValue>(
            codec,
            async (ctx, ct) =>
            {
                var values = new List<string>();
                await foreach (var item in ctx.ReadAllAsync(ct))
                {
                    values.Add(item.Value);
                }

                return Response<StringValue>.Create(
                    new StringValue { Value = string.Join(",", values) },
                    new ResponseMeta());
            });

        var ct = TestContext.Current.CancellationToken;
        await channel.Writer.WriteAsync(codec.EncodeRequest(new StringValue { Value = "a" }, meta).Value, ct);
        await channel.Writer.WriteAsync(codec.EncodeRequest(new StringValue { Value = "b" }, meta).Value, ct);
        channel.Writer.TryComplete();

        var result = await handler(context, ct);
        Assert.True(result.IsSuccess);
        var response = result.Value;
        Assert.Equal(codec.Encoding, response.Meta.Encoding);
        var decoded = codec.DecodeResponse(response.Body, response.Meta);
        Assert.True(decoded.IsSuccess);
        Assert.Equal("a,b", decoded.Value.Value);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ClientStreamHandler_HandlerExceptionReturnsInternalError()
    {
        var codec = CreateCodec();
        var meta = CreateRequestMeta(codec);
        var channel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        var context = new ClientStreamRequestContext(meta, channel.Reader);

        var handler = ProtobufCallAdapters.CreateClientStreamHandler<StringValue, StringValue>(
            codec,
            (_, _) => throw new InvalidOperationException("fail"));

        channel.Writer.TryComplete();

        var result = await handler(context, TestContext.Current.CancellationToken);
        Assert.True(result.IsFailure);
        Assert.Equal(OmniRelayStatusCode.Internal, OmniRelayErrorAdapter.ToStatus(result.Error!));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task DuplexHandler_ReadsAndWritesMessages()
    {
        var codec = CreateCodec();
        var meta = CreateRequestMeta(codec);
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        var handler = ProtobufCallAdapters.CreateDuplexHandler<StringValue, StringValue>(
            codec,
            async (ctx, ct) =>
            {
                ctx.ResponseMeta = new ResponseMeta(transport: "duplex");
                await foreach (var message in ctx.ReadAllAsync(ct))
                {
                    await ctx.WriteAsync(new StringValue { Value = message.Value.ToUpperInvariant() }, ct);
                }
            });

        var result = await handler(request, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        var call = Assert.IsAssignableFrom<IDuplexStreamCall>(result.Value);

        var ct = TestContext.Current.CancellationToken;
        var payload = codec.EncodeRequest(new StringValue { Value = "foo" }, meta).Value;
        await call.RequestWriter.WriteAsync(payload, ct);
        await call.CompleteRequestsAsync(cancellationToken: ct);

        var responsePayload = await call.ResponseReader.ReadAsync(ct);
        var decoded = codec.DecodeResponse(responsePayload, call.ResponseMeta);
        Assert.True(decoded.IsSuccess);
        Assert.Equal("FOO", decoded.Value.Value);
        Assert.Equal("duplex", call.ResponseMeta.Transport);
        Assert.Equal(codec.Encoding, call.ResponseMeta.Encoding);
        Assert.False(await call.ResponseReader.WaitToReadAsync(ct));
        await call.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task DuplexHandler_HandlerExceptionCompletesWithError()
    {
        var codec = CreateCodec();
        var meta = CreateRequestMeta(codec);
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        var handler = ProtobufCallAdapters.CreateDuplexHandler<StringValue, StringValue>(
            codec,
            (_, _) => throw new InvalidOperationException("boom"));

        var result = await handler(request, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        var call = Assert.IsAssignableFrom<IDuplexStreamCall>(result.Value);

        var closed = await Assert.ThrowsAsync<ChannelClosedException>(() => call.ResponseReader.ReadAsync(TestContext.Current.CancellationToken).AsTask());
        var ex = Assert.IsType<OmniRelayException>(closed.InnerException);
        Assert.Equal(OmniRelayStatusCode.Internal, ex.StatusCode);
        await call.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task DuplexContext_WriteAsyncEncodingFailureCompletesWithError()
    {
        var codec = CreateCodec();
        var meta = CreateRequestMeta(codec);
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

        var handler = ProtobufCallAdapters.CreateDuplexHandler<StringValue, StringValue>(
            codec,
            async (ctx, ct) =>
            {
                ctx.ResponseMeta = new ResponseMeta(encoding: "invalid");
                await Assert.ThrowsAsync<OmniRelayException>(async () => await ctx.WriteAsync(new StringValue { Value = "x" }, ct));
            });

        var result = await handler(request, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        var call = Assert.IsAssignableFrom<IDuplexStreamCall>(result.Value);

        var closed = await Assert.ThrowsAsync<ChannelClosedException>(() => call.ResponseReader.ReadAsync(TestContext.Current.CancellationToken).AsTask());
        var ex = Assert.IsType<OmniRelayException>(closed.InnerException);
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, ex.StatusCode);
        await call.DisposeAsync();
    }
}
