using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using Polymer.Core;
using Polymer.Core.Middleware;
using Polymer.Core.Transport;
using Xunit;
using static Hugo.Go;

namespace Polymer.Tests.Core;

public class MiddlewareComposerTests
{
    [Fact]
    public async Task ComposeUnaryOutbound_ExecutesInRegistrationOrder()
    {
        var transcript = new List<string>();
        var middleware = new IUnaryOutboundMiddleware[]
        {
            new TrackingUnaryOutboundMiddleware("a", transcript),
            new TrackingUnaryOutboundMiddleware("b", transcript)
        };

        var terminal = new UnaryOutboundDelegate((_, _) =>
        {
            transcript.Add("terminal");
            return ValueTask.FromResult<Result<Response<ReadOnlyMemory<byte>>>>(
                Ok(new Response<ReadOnlyMemory<byte>>(new ResponseMeta(), ReadOnlyMemory<byte>.Empty)));
        });

        var pipeline = MiddlewareComposer.ComposeUnaryOutbound(middleware, terminal);

        var request = new Request<ReadOnlyMemory<byte>>(new RequestMeta(service: "svc", procedure: "echo"), ReadOnlyMemory<byte>.Empty);
        var result = await pipeline(request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new[]
            {
                "before:a",
                "before:b",
                "terminal",
                "after:b",
                "after:a"
            },
            transcript);
    }

    [Fact]
    public void ComposeOnewayOutbound_WithNoMiddlewareReturnsTerminal()
    {
        OnewayOutboundDelegate terminal = static (_, _) => ValueTask.FromResult<Result<OnewayAck>>(Ok(OnewayAck.Ack()));

        var composed = MiddlewareComposer.ComposeOnewayOutbound(null, terminal);

        Assert.Same(terminal, composed);
    }

    [Fact]
    public async Task ComposeClientStreamOutbound_ExecutesInRegistrationOrder()
    {
        var transcript = new List<string>();
        var middleware = new IClientStreamOutboundMiddleware[]
        {
            new TrackingClientStreamOutboundMiddleware("a", transcript),
            new TrackingClientStreamOutboundMiddleware("b", transcript)
        };

        var terminal = new ClientStreamOutboundDelegate((meta, _) =>
        {
            transcript.Add("terminal");
            return ValueTask.FromResult<Result<IClientStreamTransportCall>>(Ok<IClientStreamTransportCall>(new StubClientStreamTransportCall(meta)));
        });

        var pipeline = MiddlewareComposer.ComposeClientStreamOutbound(middleware, terminal);

        var meta = new RequestMeta(service: "svc", procedure: "aggregate");
        var result = await pipeline(meta, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new[]
            {
                "before:a",
                "before:b",
                "terminal",
                "after:b",
                "after:a"
            },
            transcript);
    }

    private sealed class TrackingUnaryOutboundMiddleware(string name, List<string> transcript) : IUnaryOutboundMiddleware
    {
        private readonly string _name = name;
        private readonly List<string> _transcript = transcript;

        public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken,
            UnaryOutboundDelegate next)
        {
            _transcript.Add($"before:{_name}");
            var result = await next(request, cancellationToken).ConfigureAwait(false);
            _transcript.Add($"after:{_name}");
            return result;
        }
    }

    private sealed class TrackingClientStreamOutboundMiddleware(string name, List<string> transcript) : IClientStreamOutboundMiddleware
    {
        private readonly string _name = name;
        private readonly List<string> _transcript = transcript;

        public async ValueTask<Result<IClientStreamTransportCall>> InvokeAsync(
            RequestMeta requestMeta,
            CancellationToken cancellationToken,
            ClientStreamOutboundDelegate next)
        {
            _transcript.Add($"before:{_name}");
            var result = await next(requestMeta, cancellationToken).ConfigureAwait(false);
            _transcript.Add($"after:{_name}");
            return result;
        }
    }

    private sealed class StubClientStreamTransportCall(RequestMeta meta) : IClientStreamTransportCall
    {
        public RequestMeta RequestMeta { get; } = meta;

        public ResponseMeta ResponseMeta { get; private set; } = new ResponseMeta();

        public Task<Result<Response<ReadOnlyMemory<byte>>>> Response => Task.FromResult<Result<Response<ReadOnlyMemory<byte>>>>(
            Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, ResponseMeta)));

        public ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
