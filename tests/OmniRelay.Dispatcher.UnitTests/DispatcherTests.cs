using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Channels;
using System.Threading.Tasks;
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
    public void Register_WithServiceMismatch_ReturnsError()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));
        var spec = new UnaryProcedureSpec(
            "other",
            "proc",
            (_, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))));

        var result = dispatcher.Register(spec);

        Assert.True(result.IsFailure);
        Assert.Equal(OmniRelayStatusCode.InvalidArgument, OmniRelayErrorAdapter.ToStatus(result.Error!));
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
        }).ThrowIfFailure();

        var ct = TestContext.Current.CancellationToken;
        var result = await dispatcher.InvokeUnaryAsync("echo", TestHelpers.CreateRequest(), ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "global", "local", "handler" }, invocations);
    }

    [Fact]
    public void ClientConfig_WithUnknownService_ReturnsError()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));

        var result = dispatcher.ClientConfig("remote");

        Assert.True(result.IsFailure);
        Assert.Equal(OmniRelayStatusCode.NotFound, OmniRelayErrorAdapter.ToStatus(result.Error!));
    }

    [Fact]
    public void ClientConfig_WithLocalService_ReturnsEmptyConfiguration()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));
        var config = dispatcher.ClientConfigOrThrow("svc");

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
        }).ThrowIfFailure();

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
        Assert.Same(dispatcher, TestHelpers.RecordingLifecycle.BoundDispatcher);
        Assert.Contains("bind", lifecycle.Events);

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);
        Assert.Equal(DispatcherStatus.Running, dispatcher.Status);

        await dispatcher.StopOrThrowAsync(ct);
        Assert.Equal(DispatcherStatus.Stopped, dispatcher.Status);
        Assert.Contains("start", lifecycle.Events);
        Assert.Contains("stop", lifecycle.Events);
    }

    [Fact]
    public void Introspect_ReportsProceduresAndMiddleware()
    {
        var options = new DispatcherOptions("svc");
        options.AddUnaryOutbound("remote", null, Substitute.For<IUnaryOutbound>());
        options.UnaryInboundMiddleware.Add(new RecordingUnaryMiddleware("global", []));

        var dispatcher = new Dispatcher(options);
        dispatcher.RegisterUnary("echo", builder => builder.Handle((_, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))))).ThrowIfFailure();

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
        }).ThrowIfFailure();

        var ct = TestContext.Current.CancellationToken;
        var result = await dispatcher.InvokeClientStreamAsync("collect", new RequestMeta(), ct);

        Assert.True(result.IsSuccess);
        await result.Value.DisposeAsync();
    }

    [Fact]
    public async Task StartAsync_WhenLifecycleFails_StopsPreviouslyStartedComponents()
    {
        var options = new DispatcherOptions("svc");
        var first = new TestHelpers.RecordingLifecycle();
        options.AddLifecycle("first", first);
        options.AddLifecycle("second", new ThrowingLifecycle(startThrows: true));
        var dispatcher = new Dispatcher(options);

        var startResult = await dispatcher.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(startResult.IsFailure);
        Assert.Contains("start failure", startResult.Error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stop", first.Events);
        Assert.Equal(DispatcherStatus.Stopped, dispatcher.Status);
    }

    [Fact]
    public async Task StopAsync_WhenLifecycleFails_DoesNotReportStopped()
    {
        var options = new DispatcherOptions("svc");
        options.AddLifecycle("only", new ThrowingLifecycle(stopThrows: true));
        var dispatcher = new Dispatcher(options);

        var startResult = await dispatcher.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(startResult.IsSuccess);

        var stopResult = await dispatcher.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(stopResult.IsFailure);
        Assert.Equal(DispatcherStatus.Running, dispatcher.Status);
    }

    [Fact]
    public async Task StopAsync_WhileStartIsInProgress_ReturnsFailure()
    {
        var options = new DispatcherOptions("svc");
        var lifecycle = new BlockingLifecycle();
        options.AddLifecycle("blocking", lifecycle);
        var dispatcher = new Dispatcher(options);

        var startTask = dispatcher.StartAsync(TestContext.Current.CancellationToken);

        await lifecycle.Started;

        var prematureStop = await dispatcher.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(prematureStop.IsFailure);

        lifecycle.Release();
        var startResult = await startTask;
        Assert.True(startResult.IsSuccess);

        var finalStop = await dispatcher.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(finalStop.IsSuccess);
    }

    private sealed class RecordingUnaryMiddleware(string name, List<string> sink) : IUnaryInboundMiddleware
    {
        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken,
            UnaryInboundHandler next)
        {
            sink.Add(name);
            return next(request, cancellationToken);
        }
    }

    private sealed class ThrowingLifecycle : ILifecycle
    {
        private readonly bool _startThrows;
        private readonly bool _stopThrows;

        public ThrowingLifecycle(bool startThrows = false, bool stopThrows = false)
        {
            _startThrows = startThrows;
            _stopThrows = stopThrows;
        }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            if (_startThrows)
            {
                throw new InvalidOperationException("start failure");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            if (_stopThrows)
            {
                throw new InvalidOperationException("stop failure");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingLifecycle : ILifecycle
    {
        private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            _started.TrySetResult(true);
            return new ValueTask(_release.Task.WaitAsync(cancellationToken));
        }

        public void Release() => _release.TrySetResult(true);

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
