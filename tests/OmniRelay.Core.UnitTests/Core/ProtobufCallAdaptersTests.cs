using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using Xunit;

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

        result.IsSuccess.ShouldBeTrue();
        var response = result.Value;
        response.Meta.Encoding.ShouldBe(codec.Encoding);
        var decoded = codec.DecodeResponse(response.Body, response.Meta);
        decoded.IsSuccess.ShouldBeTrue();
        decoded.Value.Value.ShouldBe("PING");
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

        invoked.ShouldBeFalse();
        result.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).ShouldBe(OmniRelayStatusCode.InvalidArgument);
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

        result.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).ShouldBe(OmniRelayStatusCode.Internal);
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
                typedRequest.Body.Value.ShouldBe("hello");
                writer.ResponseMeta = new ResponseMeta(transport: "custom");
                var writeResult = await writer.WriteAsync(new StringValue { Value = "world" }, ct);
                writeResult.ThrowIfFailure();
            });

        var result = await handler(request, options, TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();

        var call = result.Value.ShouldBeAssignableTo<IStreamCall>();
        var received = await call.Responses.ReadAsync(TestContext.Current.CancellationToken);
        var decoded = codec.DecodeResponse(received, call.ResponseMeta);
        decoded.IsSuccess.ShouldBeTrue();
        decoded.Value.Value.ShouldBe("world");
        call.ResponseMeta.Encoding.ShouldBe(codec.Encoding);
        call.ResponseMeta.Transport.ShouldBe("custom");
        (await call.Responses.WaitToReadAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
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
        result.IsSuccess.ShouldBeTrue();
        var call = result.Value.ShouldBeAssignableTo<IStreamCall>();

        var closed = await Should.ThrowAsync<ChannelClosedException>(() => call.Responses.ReadAsync(TestContext.Current.CancellationToken).AsTask());
        var ex = closed.InnerException.ShouldBeOfType<OmniRelayException>();
        ex.StatusCode.ShouldBe(OmniRelayStatusCode.Internal);
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
                var writeResult = await writer.WriteAsync(new StringValue { Value = "b" }, ct);
                writeResult.IsFailure.ShouldBeTrue();
                OmniRelayErrorAdapter.ToStatus(writeResult.Error!).ShouldBe(OmniRelayStatusCode.InvalidArgument);
            });

        var result = await handler(request, options, TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();
        var call = result.Value.ShouldBeAssignableTo<IStreamCall>();

        var closed = await Should.ThrowAsync<ChannelClosedException>(() => call.Responses.ReadAsync(TestContext.Current.CancellationToken).AsTask());
        var ex = closed.InnerException.ShouldBeOfType<OmniRelayException>();
        ex.StatusCode.ShouldBe(OmniRelayStatusCode.InvalidArgument);
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
        await foreach (var messageResult in typedContext.ReadAllAsync(ct))
        {
            messages.Add(messageResult.ValueOrThrow().Value);
        }

        messages.ShouldBe(new[] { "one", "two" });
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

        var enumerated = false;
        await foreach (var result in typedContext.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            enumerated = true;
            result.IsFailure.ShouldBeTrue();
            OmniRelayErrorAdapter.ToStatus(result.Error!).ShouldBe(OmniRelayStatusCode.InvalidArgument);
            break;
        }

        enumerated.ShouldBeTrue();
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
                await foreach (var itemResult in ctx.ReadAllAsync(ct))
                {
                    values.Add(itemResult.ValueOrThrow().Value);
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
        result.IsSuccess.ShouldBeTrue();
        var response = result.Value;
        response.Meta.Encoding.ShouldBe(codec.Encoding);
        var decoded = codec.DecodeResponse(response.Body, response.Meta);
        decoded.IsSuccess.ShouldBeTrue();
        decoded.Value.Value.ShouldBe("a,b");
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
        result.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).ShouldBe(OmniRelayStatusCode.Internal);
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
                await foreach (var messageResult in ctx.ReadAllAsync(ct))
                {
                    var message = messageResult.ValueOrThrow();
                    var writeResult = await ctx.WriteAsync(new StringValue { Value = message.Value.ToUpperInvariant() }, ct);
                    writeResult.ThrowIfFailure();
                }
            });

        var result = await handler(request, TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();
        var call = result.Value.ShouldBeAssignableTo<IDuplexStreamCall>();

        var ct = TestContext.Current.CancellationToken;
        var payload = codec.EncodeRequest(new StringValue { Value = "foo" }, meta).Value;
        await call.RequestWriter.WriteAsync(payload, ct);
        await call.CompleteRequestsAsync(cancellationToken: ct);

        var responsePayload = await call.ResponseReader.ReadAsync(ct);
        var decoded = codec.DecodeResponse(responsePayload, call.ResponseMeta);
        decoded.IsSuccess.ShouldBeTrue();
        decoded.Value.Value.ShouldBe("FOO");
        call.ResponseMeta.Transport.ShouldBe("duplex");
        call.ResponseMeta.Encoding.ShouldBe(codec.Encoding);
        (await call.ResponseReader.WaitToReadAsync(ct)).ShouldBeFalse();
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
        result.IsSuccess.ShouldBeTrue();
        var call = result.Value.ShouldBeAssignableTo<IDuplexStreamCall>();

        var closed = await Should.ThrowAsync<ChannelClosedException>(() => call.ResponseReader.ReadAsync(TestContext.Current.CancellationToken).AsTask());
        var ex = closed.InnerException.ShouldBeOfType<OmniRelayException>();
        ex.StatusCode.ShouldBe(OmniRelayStatusCode.Internal);
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
                var writeResult = await ctx.WriteAsync(new StringValue { Value = "x" }, ct);
                writeResult.IsFailure.ShouldBeTrue();
                OmniRelayErrorAdapter.ToStatus(writeResult.Error!).ShouldBe(OmniRelayStatusCode.InvalidArgument);
            });

        var result = await handler(request, TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();
        var call = result.Value.ShouldBeAssignableTo<IDuplexStreamCall>();

        var closed = await Should.ThrowAsync<ChannelClosedException>(() => call.ResponseReader.ReadAsync(TestContext.Current.CancellationToken).AsTask());
        var ex = closed.InnerException.ShouldBeOfType<OmniRelayException>();
        ex.StatusCode.ShouldBe(OmniRelayStatusCode.InvalidArgument);
        await call.DisposeAsync();
    }
}
