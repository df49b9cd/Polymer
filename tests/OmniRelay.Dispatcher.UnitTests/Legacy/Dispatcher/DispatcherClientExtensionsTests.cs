using AwesomeAssertions;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Dispatcher;

public class DispatcherClientExtensionsTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void CreateUnaryClient_WithCodecAndMissingOutbound_ReturnsFailure()
    {
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(new DispatcherOptions("local"));
        var codec = new PassthroughCodec();

        var result = dispatcher.CreateUnaryClient("remote", codec);

        result.IsFailure.Should().BeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).Should().Be(OmniRelayStatusCode.NotFound);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void CreateUnaryClient_ResolvesRegisteredCodec()
    {
        var options = new DispatcherOptions("local");
        var outbound = new StubUnaryOutbound();
        options.AddUnaryOutbound("remote-service", null, outbound);
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        var codec = new PassthroughCodec();
        dispatcher.Codecs.RegisterOutbound("remote-service", "math::add", ProcedureKind.Unary, codec);

        var clientResult = dispatcher.CreateUnaryClient<int, int>("remote-service", "math::add");

        clientResult.IsSuccess.Should().BeTrue();
        var client = clientResult.Value;

        client.Should().NotBeNull();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void CreateStreamClient_MissingOutboundReturnsFailure()
    {
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(new DispatcherOptions("svc"));
        var codec = new PassthroughCodec();

        var result = dispatcher.CreateStreamClient("remote", codec);

        result.IsFailure.Should().BeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).Should().Be(OmniRelayStatusCode.NotFound);
    }

    private sealed class PassthroughCodec : ICodec<int, int>
    {
        public string Encoding => "proto";

        public Result<byte[]> EncodeRequest(int value, RequestMeta meta) =>
            Ok(new byte[] { (byte)value });

        public Result<int> DecodeRequest(ReadOnlyMemory<byte> payload, RequestMeta meta) =>
            Ok(payload.Span.Length > 0 ? payload.Span[0] : 0);

        public Result<byte[]> EncodeResponse(int value, ResponseMeta meta) =>
            Ok(new byte[] { (byte)value });

        public Result<int> DecodeResponse(ReadOnlyMemory<byte> payload, ResponseMeta meta) =>
            Ok(payload.Span.Length > 0 ? payload.Span[0] : 0);
    }

    private sealed class StubUnaryOutbound : IUnaryOutbound
    {
        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
    }
}
