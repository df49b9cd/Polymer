using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Channels;
using Hugo;
using NSubstitute;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Dispatcher.UnitTests;

public class DispatcherTests
{
    [Fact]
    public void Register_WithServiceMismatch_Throws()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));
        var spec = new UnaryProcedureSpec(
            "other",
            "proc",
            (_, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))));

        Assert.Throws<InvalidOperationException>(() => dispatcher.Register(spec));
    }

    [Fact]
    public async Task InvokeUnaryAsync_ComposesGlobalAndLocalMiddleware()
    {
        var options = new DispatcherOptions("svc");
        var invocations = new List<string>();
        options.UnaryInboundMiddleware.Add(new RecordingUnaryMiddleware("global", invocations));

        var dispatcher = new Dispatcher(options);

        dispatcher.RegisterUnary("echo", builder =>
        {
            builder.Handle(async (request, token) =>
            {
                invocations.Add("handler");
                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty));
            });
            builder.Use(new RecordingUnaryMiddleware("local", invocations));
        });

        var ct = TestContext.Current.CancellationToken;
        var result = await dispatcher.InvokeUnaryAsync("echo", TestHelpers.CreateRequest(), ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "global", "local", "handler" }, invocations);
    }

    [Fact]
    public void ClientConfig_WithUnknownService_Throws()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));

        Assert.Throws<KeyNotFoundException>(() => dispatcher.ClientConfig("remote"));
    }

    [Fact]
    public void ClientConfig_WithLocalService_ReturnsEmptyConfiguration()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));
        var config = dispatcher.ClientConfig("svc");

        Assert.Equal("svc", config.Service);
        Assert.Empty(config.Unary);
    }

    [Fact]
    public void TryGetProcedure_WithAlias_ReturnsSpec()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));

        dispatcher.RegisterUnary("primary", builder =>
        {
            builder.Handle((_, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))));
            builder.AddAlias("alias");
        });

        Assert.True(dispatcher.TryGetProcedure("alias", ProcedureKind.Unary, out var spec));
        Assert.Equal("primary", spec.Name);
    }

    [Fact]
    public async Task StartAsync_BindsDispatcherAwareComponents()
    {
        var options = new DispatcherOptions("svc");
        var lifecycle = new TestHelpers.RecordingLifecycle();
        options.AddLifecycle("component", lifecycle);

        var dispatcher = new Dispatcher(options);
        Assert.Same(dispatcher, lifecycle.BoundDispatcher);
        Assert.Contains("bind", lifecycle.Events);

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        Assert.Equal(DispatcherStatus.Running, dispatcher.Status);

        await dispatcher.StopAsync(ct);
        Assert.Equal(DispatcherStatus.Stopped, dispatcher.Status);
        Assert.Contains("start", lifecycle.Events);
        Assert.Contains("stop", lifecycle.Events);
    }

    [Fact]
    public void Introspect_ReportsProceduresAndMiddleware()
    {
        var options = new DispatcherOptions("svc");
        options.AddUnaryOutbound("remote", null, Substitute.For<IUnaryOutbound>());
        options.UnaryInboundMiddleware.Add(new RecordingUnaryMiddleware("global", new List<string>()));

        var dispatcher = new Dispatcher(options);
        dispatcher.RegisterUnary("echo", builder => builder.Handle((_, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)))));

        var snapshot = dispatcher.Introspect();

        Assert.Equal("svc", snapshot.Service);
        Assert.Equal(dispatcher.Status, snapshot.Status);
        Assert.Single(snapshot.Procedures.Unary);
        Assert.Single(snapshot.Middleware.InboundUnary);
        Assert.Single(snapshot.Outbounds);
    }

    [Fact]
    public async Task InvokeUnaryAsync_WhenMissing_ReturnsUnimplementedError()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));
        var ct = TestContext.Current.CancellationToken;
        var result = await dispatcher.InvokeUnaryAsync("missing", TestHelpers.CreateRequest(), ct);

        Assert.True(result.IsFailure);
        Assert.Equal(OmniRelayStatusCode.Unimplemented, OmniRelayErrorAdapter.ToStatus(result.Error!));
    }

    [Fact]
    public async Task InvokeClientStreamAsync_ReturnsCallHandle()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));

        dispatcher.RegisterClientStream("collect", builder =>
        {
            builder.Handle(async (context, token) =>
            {
                await foreach (var _ in context.Requests.ReadAllAsync(token))
                {
                }

                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty));
            });
        });

        var ct = TestContext.Current.CancellationToken;
        var result = await dispatcher.InvokeClientStreamAsync("collect", new RequestMeta(), ct);

        Assert.True(result.IsSuccess);
        await result.Value.DisposeAsync();
    }

    private sealed class RecordingUnaryMiddleware(string name, List<string> sink) : IUnaryInboundMiddleware
    {
        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken,
            UnaryInboundDelegate next)
        {
            sink.Add(name);
            return next(request, cancellationToken);
        }
    }
}
