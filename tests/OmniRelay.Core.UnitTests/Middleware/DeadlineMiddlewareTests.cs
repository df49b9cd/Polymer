using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Hugo;
using NSubstitute;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Middleware;

public class DeadlineMiddlewareTests
{
    private static IRequest<ReadOnlyMemory<byte>> MakeReq(RequestMeta meta) => new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task PastDeadline_FailsImmediately()
    {
        var mw = new DeadlineMiddleware();
        var meta = new RequestMeta(service: "svc", procedure: "proc", deadline: DateTimeOffset.UtcNow.AddSeconds(-1));
        UnaryOutboundHandler next = (req, ct) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));

        var res = await mw.InvokeAsync(MakeReq(meta), TestContext.Current.CancellationToken, next);
        Assert.True(res.IsFailure);
        Assert.Equal(OmniRelayStatusCode.DeadlineExceeded, OmniRelayErrorAdapter.ToStatus(res.Error!));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task TtlBelowLeadTime_FailsImmediately()
    {
        var mw = new DeadlineMiddleware(new DeadlineOptions { MinimumLeadTime = TimeSpan.FromSeconds(5) });
        var meta = new RequestMeta(service: "svc", procedure: "proc", timeToLive: TimeSpan.FromSeconds(1));
        UnaryOutboundHandler next = (req, ct) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));

        var res = await mw.InvokeAsync(MakeReq(meta), TestContext.Current.CancellationToken, next);
        Assert.True(res.IsFailure);
        Assert.Equal(OmniRelayStatusCode.DeadlineExceeded, OmniRelayErrorAdapter.ToStatus(res.Error!));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task FutureDeadline_LinksCancellationToken()
    {
        var mw = new DeadlineMiddleware();
        var meta = new RequestMeta(service: "svc", procedure: "proc", deadline: DateTimeOffset.UtcNow.AddMilliseconds(50));
        var called = false;
        UnaryOutboundHandler next = async (req, ct) =>
        {
            called = true;
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty));
        };

        var res = await mw.InvokeAsync(MakeReq(meta), TestContext.Current.CancellationToken, next);
        Assert.True(called);
        Assert.True(res.IsFailure);
        Assert.Equal(OmniRelayStatusCode.DeadlineExceeded, OmniRelayErrorAdapter.ToStatus(res.Error!));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ZeroTimeToLive_FailsImmediately()
    {
        var mw = new DeadlineMiddleware();
        var meta = new RequestMeta(service: "svc", procedure: "proc", timeToLive: TimeSpan.Zero);

        OnewayInboundHandler next = (req, ct) => ValueTask.FromResult(Ok(OnewayAck.Ack()));
        var result = await mw.InvokeAsync(MakeReq(meta), TestContext.Current.CancellationToken, next);

        Assert.True(result.IsFailure);
        Assert.Equal(OmniRelayStatusCode.DeadlineExceeded, OmniRelayErrorAdapter.ToStatus(result.Error!));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task AllShapes_WithoutDeadline_InvokeNext()
    {
        var mw = new DeadlineMiddleware();
        var meta = new RequestMeta(service: "svc", procedure: "proc");
        var request = MakeReq(meta);
        var streamOptions = new StreamCallOptions(StreamDirection.Server);
        var clientStreamContext = new ClientStreamRequestContext(meta, Channel.CreateUnbounded<ReadOnlyMemory<byte>>().Reader);

        UnaryOutboundHandler unaryOutbound = (req, ct) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        UnaryInboundHandler unaryInbound = (req, ct) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        OnewayOutboundHandler onewayOutbound = (req, ct) => ValueTask.FromResult(Ok(OnewayAck.Ack()));
        OnewayInboundHandler onewayInbound = (req, ct) => ValueTask.FromResult(Ok(OnewayAck.Ack()));
        StreamOutboundHandler streamOutbound = (req, opts, ct) => ValueTask.FromResult(Ok<IStreamCall>(ServerStreamCall.Create(req.Meta)));
        StreamInboundHandler streamInbound = (req, opts, ct) => ValueTask.FromResult(Ok<IStreamCall>(ServerStreamCall.Create(req.Meta)));
        ClientStreamInboundHandler clientStreamInbound = (ctx, ct) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        ClientStreamOutboundHandler clientStreamOutbound = (m, ct) => ValueTask.FromResult(Ok<IClientStreamTransportCall>(CreateClientStreamCall(meta)));
        DuplexOutboundHandler duplexOutbound = (req, ct) => ValueTask.FromResult(Ok<IDuplexStreamCall>(DuplexStreamCall.Create(req.Meta)));
        DuplexInboundHandler duplexInbound = (req, ct) => ValueTask.FromResult(Ok<IDuplexStreamCall>(DuplexStreamCall.Create(req.Meta)));

        Assert.True((await mw.InvokeAsync(request, TestContext.Current.CancellationToken, unaryOutbound)).IsSuccess);
        Assert.True((await mw.InvokeAsync(request, TestContext.Current.CancellationToken, unaryInbound)).IsSuccess);
        Assert.True((await mw.InvokeAsync(request, TestContext.Current.CancellationToken, onewayOutbound)).IsSuccess);
        Assert.True((await mw.InvokeAsync(request, TestContext.Current.CancellationToken, onewayInbound)).IsSuccess);
        Assert.True((await mw.InvokeAsync(request, streamOptions, TestContext.Current.CancellationToken, streamOutbound)).IsSuccess);
        Assert.True((await mw.InvokeAsync(request, streamOptions, TestContext.Current.CancellationToken, streamInbound)).IsSuccess);
        Assert.True((await mw.InvokeAsync(clientStreamContext, TestContext.Current.CancellationToken, clientStreamInbound)).IsSuccess);
        Assert.True((await mw.InvokeAsync(meta, TestContext.Current.CancellationToken, clientStreamOutbound)).IsSuccess);
        Assert.True((await mw.InvokeAsync(request, TestContext.Current.CancellationToken, duplexOutbound)).IsSuccess);
        Assert.True((await mw.InvokeAsync(request, TestContext.Current.CancellationToken, duplexInbound)).IsSuccess);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ExceptionPath_AddsExceptionMetadata()
    {
        var mw = new DeadlineMiddleware();
        var meta = new RequestMeta(service: "svc", procedure: "proc", timeToLive: TimeSpan.FromMilliseconds(250));

        UnaryInboundHandler next = async (req, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty));
        };

        var result = await mw.InvokeAsync(MakeReq(meta), TestContext.Current.CancellationToken, next);

        Assert.True(result.IsFailure);
        Assert.True(result.Error!.Metadata.ContainsKey("exception_type"));
        var exceptionType = Assert.IsType<string>(result.Error!.Metadata["exception_type"]);
        Assert.Contains("CanceledException", exceptionType);
    }

    private static IClientStreamTransportCall CreateClientStreamCall(RequestMeta meta)
    {
        var call = Substitute.For<IClientStreamTransportCall>();
        call.RequestMeta.Returns(meta);
        call.ResponseMeta.Returns(new ResponseMeta());
        call.Response.Returns(new ValueTask<Result<Response<ReadOnlyMemory<byte>>>>(
            Task.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)))));
        call.WriteAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        call.CompleteAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        call.DisposeAsync().Returns(ValueTask.CompletedTask);
        return call;
    }
}
