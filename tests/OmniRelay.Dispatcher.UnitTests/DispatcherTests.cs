using AwesomeAssertions;
using Hugo;
using NSubstitute;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Dispatcher.UnitTests;

public class DispatcherTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void Register_WithServiceMismatch_ReturnsError()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));
        var spec = new UnaryProcedureSpec(
            "other",
            "proc",
            (_, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))));

        var result = dispatcher.Register(spec);

        result.IsFailure.Should().BeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).Should().Be(OmniRelayStatusCode.InvalidArgument);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask InvokeUnaryAsync_ComposesGlobalAndLocalMiddleware()
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
        }).ValueOrChecked();

        var ct = TestContext.Current.CancellationToken;
        var result = await dispatcher.InvokeUnaryAsync("echo", TestHelpers.CreateRequest(), ct);

        result.IsSuccess.Should().BeTrue();
        invocations.Should().Equal("global", "local", "handler");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ClientConfig_WithUnknownService_ReturnsError()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));

        var result = dispatcher.ClientConfig("remote");

        result.IsFailure.Should().BeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).Should().Be(OmniRelayStatusCode.NotFound);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ClientConfig_WithLocalService_ReturnsEmptyConfiguration()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));
        var config = dispatcher.ClientConfigChecked("svc");

        config.Service.Should().Be("svc");
        config.Unary.Should().BeEmpty();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryGetProcedure_WithAlias_ReturnsSpec()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));

        dispatcher.RegisterUnary("primary", builder =>
        {
            builder.Handle((_, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))));
            builder.AddAlias("alias");
        }).ValueOrChecked();

        dispatcher.TryGetProcedure("alias", ProcedureKind.Unary, out var spec).Should().BeTrue();
        spec.Name.Should().Be("primary");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask StartAsync_BindsDispatcherAwareComponents()
    {
        var options = new DispatcherOptions("svc");
        var lifecycle = new TestHelpers.RecordingLifecycle();
        options.AddLifecycle("component", lifecycle);

        var dispatcher = new Dispatcher(options);
        TestHelpers.RecordingLifecycle.BoundDispatcher.Should().BeSameAs(dispatcher);
        lifecycle.Events.Should().Contain("bind");

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsyncChecked(ct);
        dispatcher.Status.Should().Be(DispatcherStatus.Running);

        await dispatcher.StopAsyncChecked(ct);
        dispatcher.Status.Should().Be(DispatcherStatus.Stopped);
        lifecycle.Events.Should().Contain("start");
        lifecycle.Events.Should().Contain("stop");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Introspect_ReportsProceduresAndMiddleware()
    {
        var options = new DispatcherOptions("svc");
        options.AddUnaryOutbound("remote", null, Substitute.For<IUnaryOutbound>());
        options.UnaryInboundMiddleware.Add(new RecordingUnaryMiddleware("global", []));

        var dispatcher = new Dispatcher(options);
        dispatcher.RegisterUnary("echo", builder => builder.Handle((_, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))))).ValueOrChecked();

        var snapshot = dispatcher.Introspect();

        snapshot.Service.Should().Be("svc");
        snapshot.Status.Should().Be(dispatcher.Status);
        snapshot.Procedures.Unary.Should().HaveCount(1);
        snapshot.Middleware.InboundUnary.Should().HaveCount(1);
        snapshot.Outbounds.Should().HaveCount(1);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask InvokeUnaryAsync_WhenMissing_ReturnsUnimplementedError()
    {
        var dispatcher = new Dispatcher(new DispatcherOptions("svc"));
        var ct = TestContext.Current.CancellationToken;
        var result = await dispatcher.InvokeUnaryAsync("missing", TestHelpers.CreateRequest(), ct);

        result.IsFailure.Should().BeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).Should().Be(OmniRelayStatusCode.Unimplemented);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask InvokeClientStreamAsync_ReturnsCallHandle()
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
        }).ValueOrChecked();

        var ct = TestContext.Current.CancellationToken;
        var result = await dispatcher.InvokeClientStreamAsync("collect", new RequestMeta(), ct);

        result.IsSuccess.Should().BeTrue();
        await result.Value.DisposeAsync();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask StartAsync_WhenLifecycleFails_StopsPreviouslyStartedComponents()
    {
        var options = new DispatcherOptions("svc");
        var first = new TestHelpers.RecordingLifecycle();
        options.AddLifecycle("first", first);
        options.AddLifecycle("second", new ThrowingLifecycle(startThrows: true));
        var dispatcher = new Dispatcher(options);

        var startResult = await dispatcher.StartAsync(TestContext.Current.CancellationToken);
        startResult.IsFailure.Should().BeTrue();
        startResult.Error!.Message.Should().ContainEquivalentOf("start failure");
        first.Events.Should().Contain("stop");
        dispatcher.Status.Should().Be(DispatcherStatus.Stopped);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask StopAsync_WhenLifecycleFails_DoesNotReportStopped()
    {
        var options = new DispatcherOptions("svc");
        options.AddLifecycle("only", new ThrowingLifecycle(stopThrows: true));
        var dispatcher = new Dispatcher(options);

        var startResult = await dispatcher.StartAsync(TestContext.Current.CancellationToken);
        startResult.IsSuccess.Should().BeTrue();

        var stopResult = await dispatcher.StopAsync(TestContext.Current.CancellationToken);
        stopResult.IsFailure.Should().BeTrue();
        dispatcher.Status.Should().Be(DispatcherStatus.Running);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask StopAsync_WhileStartIsInProgress_ReturnsFailure()
    {
        var options = new DispatcherOptions("svc");
        var lifecycle = new BlockingLifecycle();
        options.AddLifecycle("blocking", lifecycle);
        var dispatcher = new Dispatcher(options);

        var startTask = dispatcher.StartAsync(TestContext.Current.CancellationToken);

        await lifecycle.Started;

        var prematureStop = await dispatcher.StopAsync(TestContext.Current.CancellationToken);
        prematureStop.IsFailure.Should().BeTrue();

        lifecycle.Release();
        var startResult = await startTask;
        startResult.IsSuccess.Should().BeTrue();

        var finalStop = await dispatcher.StopAsync(TestContext.Current.CancellationToken);
        finalStop.IsSuccess.Should().BeTrue();
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

    private sealed class ThrowingLifecycle(bool startThrows = false, bool stopThrows = false) : ILifecycle
    {
        private readonly bool _startThrows = startThrows;
        private readonly bool _stopThrows = stopThrows;

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
