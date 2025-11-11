using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using NSubstitute;
using OmniRelay.Core;
using OmniRelay.Core.Clients;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Clients;

public class ClientStreamClientTests
{
    public sealed class Req
    {
        public int V { get; init; }
    }
    public sealed class Res
    {
        public string? S { get; init; }
    }

    private sealed class TestClientStreamTransportCall : IClientStreamTransportCall
    {
        private readonly List<ReadOnlyMemory<byte>> _writes = [];
        private readonly TaskCompletionSource<Result<Response<ReadOnlyMemory<byte>>>> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TestClientStreamTransportCall(RequestMeta meta)
        {
            RequestMeta = meta;
            ResponseMeta = new ResponseMeta();
        }

        public RequestMeta RequestMeta { get; }

        public ResponseMeta ResponseMeta { get; set; }

        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> Response => new(_tcs.Task);
        public IReadOnlyList<ReadOnlyMemory<byte>> Writes => _writes;

        public ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            _writes.Add(payload);
            return ValueTask.CompletedTask;
        }

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void CompleteWith(Result<Response<ReadOnlyMemory<byte>>> result)
        {
            if (result.IsSuccess)
            {
                ResponseMeta = result.Value.Meta;
            }
            _tcs.TrySetResult(result);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Start_Write_Complete_Response_Decode_Succeeds()
    {
        var outbound = Substitute.For<IClientStreamOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("json");
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>()).Returns(ci => Ok(new byte[] { (byte)ci.Arg<Req>().V }));
        codec.DecodeResponse(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ResponseMeta>()).Returns(ci => Ok(new Res { S = Convert.ToBase64String(ci.Arg<ReadOnlyMemory<byte>>().ToArray()) }));

        var meta = new RequestMeta();
        var transportCall = new TestClientStreamTransportCall(meta);
        outbound.CallAsync(Arg.Any<RequestMeta>(), Arg.Any<CancellationToken>()).Returns(ci => ValueTask.FromResult(Ok((IClientStreamTransportCall)transportCall)));

        var client = new ClientStreamClient<Req, Res>(outbound, codec, []);
        var sessionResult = await client.StartAsync(meta, TestContext.Current.CancellationToken);
        await using var session = sessionResult.ValueOrThrow();

        var firstWrite = await session.WriteAsync(new Req { V = 10 }, TestContext.Current.CancellationToken);
        firstWrite.ThrowIfFailure();
        var secondWrite = await session.WriteAsync(new Req { V = 20 }, TestContext.Current.CancellationToken);
        secondWrite.ThrowIfFailure();
        await session.CompleteAsync(TestContext.Current.CancellationToken);

        var responseBytes = new byte[] { 99 };
        var finalMeta = new ResponseMeta { Transport = "test" };
        transportCall.CompleteWith(Ok(Response<ReadOnlyMemory<byte>>.Create(responseBytes, finalMeta)));

        var responseResult = await session.Response;
        var response = responseResult.ValueOrThrow();
        Assert.Equal(Convert.ToBase64String(responseBytes), response.Body.S);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task StartAsync_PipelineFailure_Throws()
    {
        var outbound = Substitute.For<IClientStreamOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("json");
        outbound.CallAsync(Arg.Any<RequestMeta>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Err<IClientStreamTransportCall>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "client"))));

        var client = new ClientStreamClient<Req, Res>(outbound, codec, []);
        var result = await client.StartAsync(new RequestMeta(service: "svc"), TestContext.Current.CancellationToken);
        Assert.True(result.IsFailure);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task WriteAsync_EncodeFailure_Throws()
    {
        var outbound = Substitute.For<IClientStreamOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("json");
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>()).Returns(Err<byte[]>(Error.From("encode", "bad")));

        var meta = new RequestMeta();
        var transportCall = new TestClientStreamTransportCall(meta);
        outbound.CallAsync(Arg.Any<RequestMeta>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Ok((IClientStreamTransportCall)transportCall)));

        var client = new ClientStreamClient<Req, Res>(outbound, codec, []);
        var sessionResult = await client.StartAsync(meta, TestContext.Current.CancellationToken);
        await using var session = sessionResult.ValueOrThrow();

        var writeResult = await session.WriteAsync(new Req { V = 1 }, TestContext.Current.CancellationToken);
        Assert.True(writeResult.IsFailure);
        Assert.Empty(transportCall.Writes);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Response_Failure_Throws()
    {
        var outbound = Substitute.For<IClientStreamOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("json");
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>()).Returns(Ok(new byte[] { 1 }));
        codec.DecodeResponse(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ResponseMeta>()).Returns(Ok(new Res()));

        var meta = new RequestMeta();
        var transportCall = new TestClientStreamTransportCall(meta);
        outbound.CallAsync(Arg.Any<RequestMeta>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Ok((IClientStreamTransportCall)transportCall)));

        var client = new ClientStreamClient<Req, Res>(outbound, codec, []);
        var sessionResult = await client.StartAsync(meta, TestContext.Current.CancellationToken);
        await using var session = sessionResult.ValueOrThrow();

        transportCall.CompleteWith(Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Internal, "fail", transport: "client")));

        var responseResult = await session.Response;
        Assert.True(responseResult.IsFailure);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Response_DecodeFailure_Throws()
    {
        var outbound = Substitute.For<IClientStreamOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("json");
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>()).Returns(Ok(new byte[] { 1 }));
        codec.DecodeResponse(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ResponseMeta>())
            .Returns(Err<Res>(Error.From("decode", "bad")));

        var meta = new RequestMeta();
        var transportCall = new TestClientStreamTransportCall(meta);
        outbound.CallAsync(Arg.Any<RequestMeta>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Ok((IClientStreamTransportCall)transportCall)));

        var client = new ClientStreamClient<Req, Res>(outbound, codec, []);
        var sessionResult = await client.StartAsync(meta, TestContext.Current.CancellationToken);
        await using var session = sessionResult.ValueOrThrow();

        transportCall.CompleteWith(Ok(Response<ReadOnlyMemory<byte>>.Create(new byte[] { 2 }, new ResponseMeta())));

        var responseResult = await session.Response;
        Assert.True(responseResult.IsFailure);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task StartAsync_SetsEncodingWhenMissing()
    {
        var outbound = Substitute.For<IClientStreamOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("proto");

        RequestMeta? capturedMeta = null;
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>())
            .Returns(ci =>
            {
                capturedMeta = ci.Arg<RequestMeta>();
                return Ok(new byte[] { 1 });
            });

        codec.DecodeResponse(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ResponseMeta>()).Returns(Ok(new Res()));

        var meta = new RequestMeta();
        var transportCall = new TestClientStreamTransportCall(meta);
        outbound.CallAsync(Arg.Any<RequestMeta>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Ok((IClientStreamTransportCall)transportCall)));

        var client = new ClientStreamClient<Req, Res>(outbound, codec, []);
        var sessionResult = await client.StartAsync(meta, TestContext.Current.CancellationToken);
        await using var session = sessionResult.ValueOrThrow();
        var writeResult = await session.WriteAsync(new Req { V = 5 }, TestContext.Current.CancellationToken);
        writeResult.ThrowIfFailure();
        transportCall.CompleteWith(Ok(Response<ReadOnlyMemory<byte>>.Create(new byte[] { 5 }, new ResponseMeta())));
        var responseResult = await session.Response;
        responseResult.ValueOrThrow();

        Assert.NotNull(capturedMeta);
        Assert.Equal("proto", capturedMeta!.Encoding);
    }
}
