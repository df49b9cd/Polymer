using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Hugo;
using NSubstitute;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Middleware;

public class MiddlewareComposerTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ComposeUnaryOutbound_ChainsInConfiguredOrder()
    {
        var order = new List<string>();
        var m1 = new RecordingMiddleware("m1", order);
        var m2 = new RecordingMiddleware("m2", order);
        var m3 = new RecordingMiddleware("m3", order);
        var meta = new RequestMeta(service: "svc");

        UnaryOutboundHandler terminal = (req, ct) =>
        {
            order.Add("terminal");
            return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        };

        var composed = MiddlewareComposer.ComposeUnaryOutbound([m1, m2, m3], terminal);
        var result = await composed(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        order.ShouldBe(new[]
        {
            "m1:unary-out",
            "m2:unary-out",
            "m3:unary-out",
            "terminal"
        });
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Compose_ReturnsTerminal_WhenNullOrEmpty()
    {
        var meta = new RequestMeta(service: "svc");

        UnaryOutboundHandler unaryOut = (req, ct) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        MiddlewareComposer.ComposeUnaryOutbound(null, unaryOut).ShouldBeSameAs(unaryOut);
        MiddlewareComposer.ComposeUnaryOutbound([], unaryOut).ShouldBeSameAs(unaryOut);

        UnaryInboundHandler unaryIn = (req, ct) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        MiddlewareComposer.ComposeUnaryInbound(null, unaryIn).ShouldBeSameAs(unaryIn);
        MiddlewareComposer.ComposeUnaryInbound([], unaryIn).ShouldBeSameAs(unaryIn);

        OnewayOutboundHandler onewayOut = (req, ct) => ValueTask.FromResult(Ok(OnewayAck.Ack()));
        MiddlewareComposer.ComposeOnewayOutbound(null, onewayOut).ShouldBeSameAs(onewayOut);
        MiddlewareComposer.ComposeOnewayOutbound([], onewayOut).ShouldBeSameAs(onewayOut);

        OnewayInboundHandler onewayIn = (req, ct) => ValueTask.FromResult(Ok(OnewayAck.Ack()));
        MiddlewareComposer.ComposeOnewayInbound(null, onewayIn).ShouldBeSameAs(onewayIn);
        MiddlewareComposer.ComposeOnewayInbound([], onewayIn).ShouldBeSameAs(onewayIn);

        StreamOutboundHandler streamOut = (req, opts, ct) => ValueTask.FromResult(Ok<IStreamCall>(ServerStreamCall.Create(meta)));
        MiddlewareComposer.ComposeStreamOutbound(null, streamOut).ShouldBeSameAs(streamOut);
        MiddlewareComposer.ComposeStreamOutbound([], streamOut).ShouldBeSameAs(streamOut);

        StreamInboundHandler streamIn = (req, opts, ct) => ValueTask.FromResult(Ok<IStreamCall>(ServerStreamCall.Create(meta)));
        MiddlewareComposer.ComposeStreamInbound(null, streamIn).ShouldBeSameAs(streamIn);
        MiddlewareComposer.ComposeStreamInbound([], streamIn).ShouldBeSameAs(streamIn);

        ClientStreamInboundHandler clientStreamIn = (ctx, ct) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
        MiddlewareComposer.ComposeClientStreamInbound(null, clientStreamIn).ShouldBeSameAs(clientStreamIn);
        MiddlewareComposer.ComposeClientStreamInbound([], clientStreamIn).ShouldBeSameAs(clientStreamIn);

        ClientStreamOutboundHandler clientStreamOut = (requestMeta, ct) => ValueTask.FromResult(Ok<IClientStreamTransportCall>(CreateClientStreamCall(requestMeta)));
        MiddlewareComposer.ComposeClientStreamOutbound(null, clientStreamOut).ShouldBeSameAs(clientStreamOut);
        MiddlewareComposer.ComposeClientStreamOutbound([], clientStreamOut).ShouldBeSameAs(clientStreamOut);

        DuplexInboundHandler duplexIn = (req, ct) => ValueTask.FromResult(Ok<IDuplexStreamCall>(DuplexStreamCall.Create(meta)));
        MiddlewareComposer.ComposeDuplexInbound(null, duplexIn).ShouldBeSameAs(duplexIn);
        MiddlewareComposer.ComposeDuplexInbound([], duplexIn).ShouldBeSameAs(duplexIn);

        DuplexOutboundHandler duplexOut = (req, ct) => ValueTask.FromResult(Ok<IDuplexStreamCall>(DuplexStreamCall.Create(meta)));
        MiddlewareComposer.ComposeDuplexOutbound(null, duplexOut).ShouldBeSameAs(duplexOut);
        MiddlewareComposer.ComposeDuplexOutbound([], duplexOut).ShouldBeSameAs(duplexOut);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ComposeUnaryInbound_Chains()
    {
        var order = new List<string>();
        var meta = new RequestMeta(service: "svc");
        var composed = MiddlewareComposer.ComposeUnaryInbound(
            [new RecordingMiddleware("m1", order), new RecordingMiddleware("m2", order)],
            (req, ct) =>
            {
                order.Add("terminal");
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            });

        var result = await composed(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        order.ShouldBe(new[]
        {
            "m1:unary-in",
            "m2:unary-in",
            "terminal"
        });
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ComposeOnewayOutbound_Chains()
    {
        var order = new List<string>();
        var meta = new RequestMeta(service: "svc");
        var composed = MiddlewareComposer.ComposeOnewayOutbound(
            [new RecordingMiddleware("m1", order), new RecordingMiddleware("m2", order)],
            (req, ct) =>
            {
                order.Add("terminal");
                return ValueTask.FromResult(Ok(OnewayAck.Ack()));
            });

        var result = await composed(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        order.ShouldBe(new[]
        {
            "m1:oneway-out",
            "m2:oneway-out",
            "terminal"
        });
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ComposeOnewayInbound_Chains()
    {
        var order = new List<string>();
        var meta = new RequestMeta(service: "svc");
        var composed = MiddlewareComposer.ComposeOnewayInbound(
            [new RecordingMiddleware("m1", order), new RecordingMiddleware("m2", order)],
            (req, ct) =>
            {
                order.Add("terminal");
                return ValueTask.FromResult(Ok(OnewayAck.Ack()));
            });

        var result = await composed(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        order.ShouldBe(new[]
        {
            "m1:oneway-in",
            "m2:oneway-in",
            "terminal"
        });
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ComposeStreamOutbound_Chains()
    {
        var order = new List<string>();
        var meta = new RequestMeta(service: "svc");
        var options = new StreamCallOptions(StreamDirection.Server);
        var composed = MiddlewareComposer.ComposeStreamOutbound(
            [new RecordingMiddleware("m1", order), new RecordingMiddleware("m2", order)],
            (req, opts, ct) =>
            {
                order.Add("terminal");
                return ValueTask.FromResult(Ok<IStreamCall>(ServerStreamCall.Create(meta)));
            });

        var result = await composed(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), options, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        order.ShouldBe(new[]
        {
            "m1:stream-out",
            "m2:stream-out",
            "terminal"
        });
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ComposeStreamInbound_Chains()
    {
        var order = new List<string>();
        var meta = new RequestMeta(service: "svc");
        var options = new StreamCallOptions(StreamDirection.Server);
        var composed = MiddlewareComposer.ComposeStreamInbound(
            [new RecordingMiddleware("m1", order), new RecordingMiddleware("m2", order)],
            (req, opts, ct) =>
            {
                order.Add("terminal");
                return ValueTask.FromResult(Ok<IStreamCall>(ServerStreamCall.Create(meta)));
            });

        var result = await composed(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), options, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        order.ShouldBe(new[]
        {
            "m1:stream-in",
            "m2:stream-in",
            "terminal"
        });
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ComposeClientStreamInbound_Chains()
    {
        var order = new List<string>();
        var meta = new RequestMeta(service: "svc");
        var ctx = new ClientStreamRequestContext(meta, Channel.CreateUnbounded<ReadOnlyMemory<byte>>().Reader);
        var composed = MiddlewareComposer.ComposeClientStreamInbound(
            [new RecordingMiddleware("m1", order), new RecordingMiddleware("m2", order)],
            (context, ct) =>
            {
                order.Add("terminal");
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            });

        var result = await composed(ctx, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        order.ShouldBe(new[]
        {
            "m1:client-stream-in",
            "m2:client-stream-in",
            "terminal"
        });
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ComposeClientStreamOutbound_Chains()
    {
        var order = new List<string>();
        var meta = new RequestMeta(service: "svc");
        var composed = MiddlewareComposer.ComposeClientStreamOutbound(
            [new RecordingMiddleware("m1", order), new RecordingMiddleware("m2", order)],
            (requestMeta, ct) =>
            {
                order.Add("terminal");
                return ValueTask.FromResult(Ok<IClientStreamTransportCall>(CreateClientStreamCall(requestMeta)));
            });

        var result = await composed(meta, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        order.ShouldBe(new[]
        {
            "m1:client-stream-out",
            "m2:client-stream-out",
            "terminal"
        });
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ComposeDuplexInbound_Chains()
    {
        var order = new List<string>();
        var meta = new RequestMeta(service: "svc");
        var composed = MiddlewareComposer.ComposeDuplexInbound(
            [new RecordingMiddleware("m1", order), new RecordingMiddleware("m2", order)],
            (req, ct) =>
            {
                order.Add("terminal");
                return ValueTask.FromResult(Ok<IDuplexStreamCall>(DuplexStreamCall.Create(meta)));
            });

        var result = await composed(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        order.ShouldBe(new[]
        {
            "m1:duplex-in",
            "m2:duplex-in",
            "terminal"
        });
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task ComposeDuplexOutbound_Chains()
    {
        var order = new List<string>();
        var meta = new RequestMeta(service: "svc");
        var composed = MiddlewareComposer.ComposeDuplexOutbound(
            [new RecordingMiddleware("m1", order), new RecordingMiddleware("m2", order)],
            (req, ct) =>
            {
                order.Add("terminal");
                return ValueTask.FromResult(Ok<IDuplexStreamCall>(DuplexStreamCall.Create(meta)));
            });

        var result = await composed(new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        order.ShouldBe(new[]
        {
            "m1:duplex-out",
            "m2:duplex-out",
            "terminal"
        });
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

    private sealed class RecordingMiddleware :
        IUnaryInboundMiddleware,
        IUnaryOutboundMiddleware,
        IOnewayInboundMiddleware,
        IOnewayOutboundMiddleware,
        IStreamInboundMiddleware,
        IStreamOutboundMiddleware,
        IClientStreamInboundMiddleware,
        IClientStreamOutboundMiddleware,
        IDuplexInboundMiddleware,
        IDuplexOutboundMiddleware
    {
        private readonly string _id;
        private readonly List<string> _order;

        public RecordingMiddleware(string id, List<string> order)
        {
            _id = id;
            _order = order;
        }

        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken,
            UnaryInboundHandler next)
        {
            _order.Add($"{_id}:unary-in");
            return next(request, cancellationToken);
        }

        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken,
            UnaryOutboundHandler next)
        {
            _order.Add($"{_id}:unary-out");
            return next(request, cancellationToken);
        }

        public ValueTask<Result<OnewayAck>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken,
            OnewayInboundHandler next)
        {
            _order.Add($"{_id}:oneway-in");
            return next(request, cancellationToken);
        }

        public ValueTask<Result<OnewayAck>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken,
            OnewayOutboundHandler next)
        {
            _order.Add($"{_id}:oneway-out");
            return next(request, cancellationToken);
        }

        public ValueTask<Result<IStreamCall>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            StreamCallOptions options,
            CancellationToken cancellationToken,
            StreamInboundHandler next)
        {
            _order.Add($"{_id}:stream-in");
            return next(request, options, cancellationToken);
        }

        public ValueTask<Result<IStreamCall>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            StreamCallOptions options,
            CancellationToken cancellationToken,
            StreamOutboundHandler next)
        {
            _order.Add($"{_id}:stream-out");
            return next(request, options, cancellationToken);
        }

        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
            ClientStreamRequestContext context,
            CancellationToken cancellationToken,
            ClientStreamInboundHandler next)
        {
            _order.Add($"{_id}:client-stream-in");
            return next(context, cancellationToken);
        }

        public ValueTask<Result<IClientStreamTransportCall>> InvokeAsync(
            RequestMeta requestMeta,
            CancellationToken cancellationToken,
            ClientStreamOutboundHandler next)
        {
            _order.Add($"{_id}:client-stream-out");
            return next(requestMeta, cancellationToken);
        }

        public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken,
            DuplexInboundHandler next)
        {
            _order.Add($"{_id}:duplex-in");
            return next(request, cancellationToken);
        }

        public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken,
            DuplexOutboundHandler next)
        {
            _order.Add($"{_id}:duplex-out");
            return next(request, cancellationToken);
        }
    }
}
