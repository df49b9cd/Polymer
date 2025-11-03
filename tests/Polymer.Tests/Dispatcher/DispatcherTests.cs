using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using Hugo;
using Polymer.Core;
using Polymer.Core.Middleware;
using Polymer.Core.Transport;
using Polymer.Dispatcher;
using Polymer.Errors;
using Xunit;
using static Hugo.Go;

namespace Polymer.Tests.Dispatcher;

public class DispatcherTests
{
    [Fact]
    public async Task StartAsync_StartsAndStopsLifecycleComponents()
    {
        var lifecycle = new StubLifecycle();
        var options = new DispatcherOptions("keyvalue");
        options.AddLifecycle("test", lifecycle);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);

        Assert.Equal(DispatcherStatus.Created, dispatcher.Status);

        var ct = TestContext.Current.CancellationToken;

        await dispatcher.StartAsync(ct);

        Assert.Equal(DispatcherStatus.Running, dispatcher.Status);
        Assert.Equal(1, lifecycle.StartCalls);
        Assert.Equal(0, lifecycle.StopCalls);

        await dispatcher.StopAsync(ct);

        Assert.Equal(DispatcherStatus.Stopped, dispatcher.Status);
        Assert.Equal(1, lifecycle.StopCalls);
    }

    [Fact]
    public void Register_DuplicateProcedureThrows()
    {
        var options = new DispatcherOptions("payments");
        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);

        var spec = CreateUnaryProcedure("payments", "charge");

        dispatcher.Register(spec);

        Assert.Throws<InvalidOperationException>(() => dispatcher.Register(spec));
    }

    [Fact]
    public void Register_WithDifferentServiceThrows()
    {
        var options = new DispatcherOptions("catalog");
        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);

        var spec = CreateUnaryProcedure("inventory", "list");

        Assert.Throws<InvalidOperationException>(() => dispatcher.Register(spec));
    }

    [Fact]
    public async Task InvokeUnaryAsync_ResolvesProcedureAliases()
    {
        var options = new DispatcherOptions("keyvalue");
        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        var callCount = 0;

        var spec = new UnaryProcedureSpec(
            "keyvalue",
            "user::get",
            (request, cancellationToken) =>
            {
                Interlocked.Increment(ref callCount);
                var response = Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty);
                return ValueTask.FromResult(Ok(response));
            },
            aliases: new[] { "v1::user::*", "users::get" });

        dispatcher.Register(spec);

        var meta = new RequestMeta(service: "keyvalue", procedure: "v1::user::get", transport: "test");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var ct = TestContext.Current.CancellationToken;

        var result = await dispatcher.InvokeUnaryAsync("v1::user::get", request, ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, callCount);

        var snapshot = dispatcher.Introspect();
        var descriptor = Assert.Single(snapshot.Procedures.Unary);
        Assert.Contains("v1::user::*", descriptor.Aliases);
        Assert.Contains("users::get", descriptor.Aliases);

        var aliasMeta = new RequestMeta(service: "keyvalue", procedure: "users::get", transport: "test");
        var aliasRequest = new Request<ReadOnlyMemory<byte>>(aliasMeta, ReadOnlyMemory<byte>.Empty);

        var aliasResult = await dispatcher.InvokeUnaryAsync("users::get", aliasRequest, ct);

        Assert.True(aliasResult.IsSuccess);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task RegisterUnary_BuilderConfiguresPipelineAndMetadata()
    {
        var order = new List<string>();
        var options = new DispatcherOptions("keyvalue");
        options.UnaryInboundMiddleware.Add(new RecordingUnaryInboundMiddleware("global", order));

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);

        var middleware1 = new RecordingUnaryInboundMiddleware("m1", order);
        var middleware2 = new RecordingUnaryInboundMiddleware("m2", order);

        dispatcher.RegisterUnary(
            "user::get",
            (request, cancellationToken) =>
            {
                order.Add("handler");
                var response = Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty);
                return ValueTask.FromResult(Ok(response));
            },
            builder => builder
                .WithEncoding("json")
                .AddAlias("users::get")
                .Use(middleware1)
                .Use(middleware2));

        var meta = new RequestMeta(service: "keyvalue", procedure: "user::get", transport: "test");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var result = await dispatcher.InvokeUnaryAsync("user::get", request, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "global", "m1", "m2", "handler" }, order);

        Assert.True(dispatcher.TryGetProcedure("user::get", ProcedureKind.Unary, out var spec));
        var unarySpec = Assert.IsType<UnaryProcedureSpec>(spec);
        Assert.Equal("json", unarySpec.Encoding);
        Assert.Contains("users::get", unarySpec.Aliases);
        Assert.Collection(unarySpec.Middleware,
            mw => Assert.Same(middleware1, mw),
            mw => Assert.Same(middleware2, mw));
    }

    [Fact]
    public void RegisterUnary_BuilderRequiresHandler()
    {
        var dispatcher = new Polymer.Dispatcher.Dispatcher(new DispatcherOptions("edge"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            dispatcher.RegisterUnary("missing", builder => builder.WithEncoding("json")));

        Assert.Contains("Handle", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterStream_BuilderConfiguresMetadata()
    {
        var dispatcher = new Polymer.Dispatcher.Dispatcher(new DispatcherOptions("streaming"));
        var metadata = new StreamIntrospectionMetadata(
            new StreamChannelMetadata(StreamDirection.Server, "bounded-channel", Capacity: 32, TracksMessageCount: true));

        dispatcher.RegisterStream(
            "events::subscribe",
            (request, options, cancellationToken) =>
                ValueTask.FromResult(Err<IStreamCall>(PolymerErrorAdapter.FromStatus(PolymerStatusCode.Unimplemented, "stub"))),
            builder => builder
                .WithEncoding("json")
                .AddAliases(new[] { "events::watch" })
                .WithMetadata(metadata));

        Assert.True(dispatcher.TryGetProcedure("events::subscribe", ProcedureKind.Stream, out var spec));
        var streamSpec = Assert.IsType<StreamProcedureSpec>(spec);
        Assert.Equal("json", streamSpec.Encoding);
        Assert.Equal(metadata, streamSpec.Metadata);
        Assert.Contains("events::watch", streamSpec.Aliases);
    }

    [Fact]
    public void ClientConfig_ReturnsOutboundsAndMiddleware()
    {
        var unaryOutbound = new StubUnaryOutbound();
        var unaryMiddleware = new PassthroughUnaryOutboundMiddleware();

        var options = new DispatcherOptions("frontend");
        options.AddUnaryOutbound("backend", null, unaryOutbound);
        options.UnaryOutboundMiddleware.Add(unaryMiddleware);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);

        var config = dispatcher.ClientConfig("backend");

        Assert.Equal("backend", config.Service);
        Assert.True(config.TryGetUnary(null, out var resolved));
        Assert.Same(unaryOutbound, resolved);
        Assert.Contains(unaryOutbound, config.Unary.Values);
        Assert.Contains(unaryMiddleware, config.UnaryMiddleware);
        Assert.Empty(config.ClientStream);
        Assert.Empty(config.Duplex);
        Assert.Empty(config.ClientStreamMiddleware);
        Assert.Empty(config.DuplexMiddleware);
        Assert.Empty(config.ClientStreamMiddleware);
    }

    [Fact]
    public void ClientConfig_UnknownServiceThrows()
    {
        var dispatcher = new Polymer.Dispatcher.Dispatcher(new DispatcherOptions("frontend"));

        Assert.Throws<KeyNotFoundException>(() => dispatcher.ClientConfig("missing"));
    }

    [Fact]
    public async Task InvokeClientStreamAsync_ProcessesRequestAndCompletesResponse()
    {
        var dispatcher = new Polymer.Dispatcher.Dispatcher(new DispatcherOptions("keyvalue"));

        dispatcher.Register(new ClientStreamProcedureSpec(
            "keyvalue",
            "aggregate",
            async (context, cancellationToken) =>
            {
                var totalBytes = 0;
                await foreach (var payload in context.Requests.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    totalBytes += payload.Length;
                }

                var responseMeta = new ResponseMeta(encoding: "application/octet-stream");
                var response = Response<ReadOnlyMemory<byte>>.Create(BitConverter.GetBytes(totalBytes), responseMeta);
                return Ok(response);
            }));

        var requestMeta = new RequestMeta(service: "keyvalue", procedure: "aggregate", transport: "test");
        var ct = TestContext.Current.CancellationToken;
        var result = await dispatcher.InvokeClientStreamAsync("aggregate", requestMeta, ct);

        Assert.True(result.IsSuccess);

        await using var call = result.Value;

        await call.Requests.WriteAsync(new byte[] { 0x01, 0x02 }, ct);
        await call.Requests.WriteAsync(new byte[] { 0x03 }, ct);
        await call.CompleteWriterAsync(cancellationToken: ct);

        var responseResult = await call.Response;

        Assert.True(responseResult.IsSuccess);
        Assert.Equal("application/octet-stream", call.ResponseMeta.Encoding);
        var count = BitConverter.ToInt32(responseResult.Value.Body.Span);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task InvokeClientStreamAsync_WhenProcedureMissingReturnsError()
    {
        var dispatcher = new Polymer.Dispatcher.Dispatcher(new DispatcherOptions("keyvalue"));
        var requestMeta = new RequestMeta(service: "keyvalue", procedure: "missing", transport: "test");
        var ct = TestContext.Current.CancellationToken;

        var result = await dispatcher.InvokeClientStreamAsync("missing", requestMeta, ct);

        Assert.True(result.IsFailure);
        Assert.Equal(PolymerStatusCode.Unimplemented, PolymerErrorAdapter.ToStatus(result.Error!));
    }

    [Fact]
    public async Task Introspect_ReportsCurrentState()
    {
        var lifecycle = new StubLifecycle();
        var unaryInbound = new PassthroughUnaryInboundMiddleware();
        var unaryOutbound = new PassthroughUnaryOutboundMiddleware();
        var options = new DispatcherOptions("keyvalue");
        options.AddLifecycle("test", lifecycle);
        options.UnaryInboundMiddleware.Add(unaryInbound);
        options.UnaryOutboundMiddleware.Add(unaryOutbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        dispatcher.Register(CreateUnaryProcedure("keyvalue", "get"));

        var beforeStart = dispatcher.Introspect();
        Assert.Equal(DispatcherStatus.Created, beforeStart.Status);
        Assert.Single(beforeStart.Procedures.Unary);
        Assert.Equal("get", beforeStart.Procedures.Unary[0].Name);
        Assert.Empty(beforeStart.Middleware.InboundClientStream);
        Assert.Empty(beforeStart.Middleware.OutboundClientStream);
        Assert.Empty(beforeStart.Middleware.InboundDuplex);
        Assert.Empty(beforeStart.Middleware.OutboundDuplex);

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        var snapshot = dispatcher.Introspect();

        Assert.Equal("keyvalue", snapshot.Service);
        Assert.Equal(DispatcherStatus.Running, snapshot.Status);
        Assert.Contains(snapshot.Components, static component => component.Name == "test");
        Assert.Contains(snapshot.Middleware.InboundUnary, static typeName => typeName.Contains(nameof(PassthroughUnaryInboundMiddleware), StringComparison.Ordinal));
        Assert.Contains(snapshot.Middleware.OutboundUnary, static typeName => typeName.Contains(nameof(PassthroughUnaryOutboundMiddleware), StringComparison.Ordinal));
        Assert.Empty(snapshot.Middleware.InboundClientStream);
        Assert.Empty(snapshot.Middleware.OutboundClientStream);
        Assert.Empty(snapshot.Middleware.InboundDuplex);
        Assert.Empty(snapshot.Middleware.OutboundDuplex);

        await dispatcher.StopAsync(ct);

        var afterStop = dispatcher.Introspect();
        Assert.Equal(DispatcherStatus.Stopped, afterStop.Status);
        Assert.Empty(afterStop.Middleware.InboundClientStream);
        Assert.Empty(afterStop.Middleware.OutboundClientStream);
        Assert.Empty(afterStop.Middleware.InboundDuplex);
        Assert.Empty(afterStop.Middleware.OutboundDuplex);
        Assert.Empty(afterStop.Middleware.OutboundClientStream);
    }

    private static UnaryProcedureSpec CreateUnaryProcedure(string service, string procedure) =>
        new(
            service,
            procedure,
            static (request, cancellationToken) =>
            {
                var response = Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty);
                return ValueTask.FromResult(Ok(response));
            });

    private sealed class StubLifecycle : ILifecycle
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubUnaryOutbound : IUnaryOutbound
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(PolymerErrorAdapter.FromStatus(PolymerStatusCode.Unavailable, "not-implemented")));

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PassthroughUnaryOutboundMiddleware : IUnaryOutboundMiddleware
    {
        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken,
            UnaryOutboundDelegate next) => next(request, cancellationToken);
    }

    private sealed class PassthroughUnaryInboundMiddleware : IUnaryInboundMiddleware
    {
        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken,
            UnaryInboundDelegate next) => next(request, cancellationToken);
    }

    private sealed class RecordingUnaryInboundMiddleware(string name, IList<string> order) : IUnaryInboundMiddleware
    {
        private readonly string _name = name;
        private readonly IList<string> _order = order ?? throw new ArgumentNullException(nameof(order));

        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken,
            UnaryInboundDelegate next)
        {
            _order.Add(_name);
            return next(request, cancellationToken);
        }
    }
}
