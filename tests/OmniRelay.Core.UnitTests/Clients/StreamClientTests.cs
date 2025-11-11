using System;
using System.Collections.Generic;
using System.Linq;
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

public class StreamClientTests
{
    public sealed class Req
    {
        public int V { get; init; }
    }
    public sealed class Res
    {
        public string? S { get; init; }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CallAsync_Yields_Decoded_Responses()
    {
        var outbound = Substitute.For<IStreamOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("json");
        var encoded = new byte[] { 7 };
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>()).Returns(Ok(encoded));
        codec.DecodeResponse(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ResponseMeta>())
            .Returns(ci => Ok(new Res { S = Convert.ToBase64String(ci.Arg<ReadOnlyMemory<byte>>().ToArray()) }));

        var meta = new RequestMeta();
        var serverCall = ServerStreamCall.Create(meta, new ResponseMeta { Transport = "test" });
        outbound.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<StreamCallOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(Ok((IStreamCall)serverCall)));

        var client = new StreamClient<Req, Res>(outbound, codec, []);
        var options = new StreamCallOptions(StreamDirection.Server);

        var collectedTask = Task.Run(async () =>
        {
            var coll = new List<Response<Res>>();
            await foreach (var res in client.CallAsync(new Request<Req>(meta, new Req { V = 1 }), options, TestContext.Current.CancellationToken))
            {
                coll.Add(res.ValueOrThrow());
            }
            return coll;
        }, TestContext.Current.CancellationToken);

        // Emit two payloads and complete
        await serverCall.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        await serverCall.WriteAsync(new byte[] { 4, 5 }, TestContext.Current.CancellationToken);
        await serverCall.CompleteAsync(null, TestContext.Current.CancellationToken);

        var results = await collectedTask;
        Assert.Equal(2, results.Count);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), results[0].Body.S);
        Assert.Equal(Convert.ToBase64String(new byte[] { 4, 5 }), results[1].Body.S);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CallAsync_EncodeFailure_Throws_OmniRelayException()
    {
        var outbound = Substitute.For<IStreamOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("json");
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>()).Returns(Err<byte[]>(Error.From("bad", "invalid-argument")));

        var client = new StreamClient<Req, Res>(outbound, codec, []);
        var options = new StreamCallOptions(StreamDirection.Server);
        var enumerated = false;
        await foreach (var result in client.CallAsync(Request<Req>.Create(new Req()), options, TestContext.Current.CancellationToken))
        {
            enumerated = true;
            Assert.True(result.IsFailure);
            Assert.Equal("invalid-argument", result.Error!.Code);
            break;
        }

        Assert.True(enumerated);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CallAsync_PipelineFailure_Throws()
    {
        var outbound = Substitute.For<IStreamOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("json");
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>()).Returns(Ok(new byte[] { 1 }));
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "shadow-fail", transport: "stream");
        outbound.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<StreamCallOptions>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Err<IStreamCall>(error)));

        var client = new StreamClient<Req, Res>(outbound, codec, []);
        var options = new StreamCallOptions(StreamDirection.Server);

        var enumerated = false;
        await foreach (var result in client.CallAsync(Request<Req>.Create(new Req()), options, TestContext.Current.CancellationToken))
        {
            enumerated = true;
            Assert.True(result.IsFailure);
            Assert.Equal(OmniRelayStatusCode.Unavailable, OmniRelayErrorAdapter.ToStatus(result.Error!));
            break;
        }

        Assert.True(enumerated);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CallAsync_DecodeFailure_CompletesStreamAndThrows()
    {
        var outbound = Substitute.For<IStreamOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("json");
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>()).Returns(Ok(new byte[] { 1 }));
        codec.DecodeResponse(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ResponseMeta>())
            .Returns(Err<Res>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Internal, "decode", transport: "test")));

        var meta = new RequestMeta();
        var call = ServerStreamCall.Create(meta, new ResponseMeta { Transport = "test" });
        outbound.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<StreamCallOptions>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Ok((IStreamCall)call)));

        var client = new StreamClient<Req, Res>(outbound, codec, []);
        var options = new StreamCallOptions(StreamDirection.Server);

        var enumeration = Task.Run(async () =>
        {
            await foreach (var result in client.CallAsync(new Request<Req>(meta, new Req()), options, TestContext.Current.CancellationToken))
            {
                Assert.True(result.IsFailure);
                break;
            }
        }, TestContext.Current.CancellationToken);

        await call.WriteAsync(new byte[] { 9 }, TestContext.Current.CancellationToken);

        await enumeration;
        Assert.Equal(StreamCompletionStatus.Faulted, call.Context.CompletionStatus);
        Assert.NotNull(call.Context.CompletionError);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CallAsync_SetsEncodingWhenMissing()
    {
        var outbound = Substitute.For<IStreamOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("proto");

        RequestMeta? capturedMeta = null;
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>())
            .Returns(ci =>
            {
                capturedMeta = ci.Arg<RequestMeta>();
                return Ok(new byte[] { 42 });
            });

        codec.DecodeResponse(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ResponseMeta>()).Returns(Ok(new Res()));

        var meta = new RequestMeta();
        var call = ServerStreamCall.Create(meta, new ResponseMeta());
        outbound.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<StreamCallOptions>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Ok((IStreamCall)call)));

        var client = new StreamClient<Req, Res>(outbound, codec, []);
        var options = new StreamCallOptions(StreamDirection.Server);

        var iterate = Task.Run(async () =>
        {
            await foreach (var result in client.CallAsync(new Request<Req>(meta, new Req()), options, TestContext.Current.CancellationToken))
            {
                Assert.True(result.IsSuccess);
            }
        }, TestContext.Current.CancellationToken);

        await call.CompleteAsync(null, TestContext.Current.CancellationToken);
        await iterate;

        Assert.NotNull(capturedMeta);
        Assert.Equal("proto", capturedMeta!.Encoding);
    }
}
