using Hugo;
using NSubstitute;
using OmniRelay.Core.Clients;
using OmniRelay.Core.Transport;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Clients;

public class OnewayClientTests
{
    public sealed class Req
    {
        public string? V { get; init; }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CallAsync_Success_Encodes_InvokesOutbound()
    {
        var outbound = Substitute.For<IOnewayOutbound>();
        var codec = Substitute.For<ICodec<Req, object>>();
        codec.Encoding.Returns("json");
        var encoded = new byte[] { 1, 2 };
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>()).Returns(Ok(encoded));

        outbound.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(Ok(OnewayAck.Ack(new ResponseMeta()))));

        var client = new OnewayClient<Req>(outbound, codec, []);
        var result = await client.CallAsync(Request<Req>.Create(new Req { V = "x" }), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        await outbound.Received(1).CallAsync(
            Arg.Is<IRequest<ReadOnlyMemory<byte>>>(r => r.Meta.Encoding == "json" && r.Body.ToArray().SequenceEqual(encoded)),
            Arg.Any<CancellationToken>());
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CallAsync_Encode_Failure_Propagates()
    {
        var outbound = Substitute.For<IOnewayOutbound>();
        var codec = Substitute.For<ICodec<Req, object>>();
        codec.Encoding.Returns("json");
        var err = Error.From("encode failed", "invalid-argument");
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>()).Returns(Err<byte[]>(err));

        var client = new OnewayClient<Req>(outbound, codec, []);
        var result = await client.CallAsync(Request<Req>.Create(new Req()), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeSameAs(err);
        await outbound.DidNotReceive().CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>());
    }
}
