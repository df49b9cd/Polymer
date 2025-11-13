using Hugo;
using NSubstitute;
using OmniRelay.Core.Clients;
using OmniRelay.Core.Transport;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Clients;

public class UnaryClientTests
{
    public sealed class Req
    {
        public int X { get; init; }
    }
    public sealed class Res
    {
        public int Y { get; init; }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CallAsync_Success_Encodes_InvokesOutbound_Decodes()
    {
        var outbound = Substitute.For<IUnaryOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("json");
        var encoded = new byte[] { 1, 2, 3 };
        var decoded = new Res { Y = 42 };
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>()).Returns(Ok(encoded));
        codec.DecodeResponse(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ResponseMeta>()).Returns(Ok(decoded));

        var meta = new RequestMeta();
        var req = new Req { X = 7 };

        var responseMeta = new ResponseMeta { Transport = "test" };
        var outboundResponse = Response<ReadOnlyMemory<byte>>.Create(encoded, responseMeta);
        outbound.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(Ok(outboundResponse)));

        var client = new UnaryClient<Req, Res>(outbound, codec, []);
        var result = await client.CallAsync(new Request<Req>(meta, req), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Body.Y.ShouldBe(42);
        await outbound.Received(1).CallAsync(
            Arg.Is<IRequest<ReadOnlyMemory<byte>>>(r => r.Meta.Encoding == "json" && r.Body.ToArray().SequenceEqual(encoded)),
            Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CallAsync_Encode_Failure_Propagates()
    {
        var outbound = Substitute.For<IUnaryOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("json");
        var err = Error.From("encode failed", "invalid-argument");
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>()).Returns(Err<byte[]>(err));

        var client = new UnaryClient<Req, Res>(outbound, codec, []);
        var result = await client.CallAsync(Request<Req>.Create(new Req()), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeSameAs(err);
        await outbound.DidNotReceive().CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CallAsync_Outbound_Failure_Propagates()
    {
        var outbound = Substitute.For<IUnaryOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("json");
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>()).Returns(Ok(new byte[] { 9 }));
        var err = Error.From("outbound failed", "unavailable");
        outbound.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(err)));

        var client = new UnaryClient<Req, Res>(outbound, codec, []);
        var result = await client.CallAsync(Request<Req>.Create(new Req()), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeSameAs(err);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CallAsync_Decode_Failure_Propagates()
    {
        var outbound = Substitute.For<IUnaryOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("json");
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>()).Returns(Ok(new byte[] { 9 }));
        var err = Error.From("decode failed", "internal");
        codec.DecodeResponse(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ResponseMeta>()).Returns(Err<Res>(err));
        var outboundResponse = Response<ReadOnlyMemory<byte>>.Create(new byte[] { 9 }, new ResponseMeta());
        outbound.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(Ok(outboundResponse)));

        var client = new UnaryClient<Req, Res>(outbound, codec, []);
        var result = await client.CallAsync(Request<Req>.Create(new Req()), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeSameAs(err);
    }
}
