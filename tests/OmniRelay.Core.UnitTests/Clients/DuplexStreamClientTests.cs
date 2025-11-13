using Hugo;
using NSubstitute;
using OmniRelay.Core.Clients;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Clients;

public class DuplexStreamClientTests
{
    public sealed class Req { public int A { get; init; } }
    public sealed class Res { public int B { get; init; } }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task StartAsync_Writes_Encodes_And_Reads_Decoded_Responses()
    {
        var outbound = Substitute.For<IDuplexOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("json");
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>()).Returns(ci => Ok(new byte[] { (byte)ci.Arg<Req>().A }));
        codec.DecodeResponse(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ResponseMeta>()).Returns(ci => Ok(new Res { B = ci.Arg<ReadOnlyMemory<byte>>().Span[0] }));

        var meta = new RequestMeta();
        var duplex = DuplexStreamCall.Create(meta, new ResponseMeta { Transport = "test" });
        outbound.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(Ok((IDuplexStreamCall)duplex)));

        var client = new DuplexStreamClient<Req, Res>(outbound, codec, []);
        var sessionResult = await client.StartAsync(meta, TestContext.Current.CancellationToken);
        await using var session = sessionResult.ValueOrThrow();

        var firstWrite = await session.WriteAsync(new Req { A = 1 }, TestContext.Current.CancellationToken);
        firstWrite.ThrowIfFailure();
        var secondWrite = await session.WriteAsync(new Req { A = 2 }, TestContext.Current.CancellationToken);
        secondWrite.ThrowIfFailure();
        await duplex.ResponseWriter.WriteAsync(new byte[] { 3 }, TestContext.Current.CancellationToken);
        await duplex.ResponseWriter.WriteAsync(new byte[] { 4 }, TestContext.Current.CancellationToken);
        await duplex.CompleteResponsesAsync(null, TestContext.Current.CancellationToken);

        var received = new List<Response<Res>>();
        await foreach (var r in session.ReadResponsesAsync(TestContext.Current.CancellationToken))
        {
            received.Add(r.ValueOrThrow());
        }

        received.Count.ShouldBe(2);
        received[0].Body.B.ShouldBe(3);
        received[1].Body.B.ShouldBe(4);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task StartAsync_PipelineFailure_Throws()
    {
        var outbound = Substitute.For<IDuplexOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("json");
        outbound.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Err<IDuplexStreamCall>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "nope", transport: "duplex"))));

        var client = new DuplexStreamClient<Req, Res>(outbound, codec, []);
        var result = await client.StartAsync(new RequestMeta(service: "svc"), TestContext.Current.CancellationToken);
        result.IsFailure.ShouldBeTrue();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task WriteAsync_EncodeFailure_Throws()
    {
        var outbound = Substitute.For<IDuplexOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("json");
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>()).Returns(Err<byte[]>(Error.From("encode", "failed")));

        var duplex = DuplexStreamCall.Create(new RequestMeta(), new ResponseMeta());
        outbound.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Ok((IDuplexStreamCall)duplex)));

        var client = new DuplexStreamClient<Req, Res>(outbound, codec, []);
        var sessionResult = await client.StartAsync(new RequestMeta(), TestContext.Current.CancellationToken);
        await using var session = sessionResult.ValueOrThrow();

        var writeResult = await session.WriteAsync(new Req(), TestContext.Current.CancellationToken);
        writeResult.IsFailure.ShouldBeTrue();
        duplex.Context.RequestMessageCount.ShouldBe(0);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ReadResponsesAsync_DecodeFailure_CompletesStreamAndThrows()
    {
        var outbound = Substitute.For<IDuplexOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("json");
        codec.EncodeRequest(Arg.Any<Req>(), Arg.Any<RequestMeta>()).Returns(Ok(new byte[] { 1 }));
        codec.DecodeResponse(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<ResponseMeta>())
            .Returns(Err<Res>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Internal, "decode", transport: "duplex")));

        var duplex = DuplexStreamCall.Create(new RequestMeta(), new ResponseMeta { Transport = "duplex" });
        outbound.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(Ok((IDuplexStreamCall)duplex)));

        var client = new DuplexStreamClient<Req, Res>(outbound, codec, []);
        var sessionResult = await client.StartAsync(new RequestMeta(), TestContext.Current.CancellationToken);
        await using var session = sessionResult.ValueOrThrow();

        var readTask = Task.Run(async () =>
        {
            await foreach (var result in session.ReadResponsesAsync(TestContext.Current.CancellationToken))
            {
                result.IsFailure.ShouldBeTrue();
                break;
            }
        }, TestContext.Current.CancellationToken);

        await duplex.ResponseWriter.WriteAsync(new byte[] { 7 }, TestContext.Current.CancellationToken);

        await readTask;
        duplex.Context.ResponseCompletionStatus.ShouldBe(StreamCompletionStatus.Faulted);
        duplex.Context.ResponseCompletionError.ShouldNotBeNull();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task StartAsync_SetsEncodingWhenMissing()
    {
        var outbound = Substitute.For<IDuplexOutbound>();
        var codec = Substitute.For<ICodec<Req, Res>>();
        codec.Encoding.Returns("proto");
        var duplex = DuplexStreamCall.Create(new RequestMeta(), new ResponseMeta());

        IRequest<ReadOnlyMemory<byte>>? captured = null;
        outbound.CallAsync(Arg.Any<IRequest<ReadOnlyMemory<byte>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.Arg<IRequest<ReadOnlyMemory<byte>>>();
                return ValueTask.FromResult(Ok((IDuplexStreamCall)duplex));
            });

        var client = new DuplexStreamClient<Req, Res>(outbound, codec, []);
        var startResult = await client.StartAsync(new RequestMeta(service: "svc"), TestContext.Current.CancellationToken);
        await using var _ = startResult.ValueOrThrow();

        captured.ShouldNotBeNull();
        captured!.Meta.Encoding.ShouldBe("proto");
    }
}
