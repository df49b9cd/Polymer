using AwesomeAssertions;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Dispatcher;

public class DispatcherTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask StartAsync_StartsAndStopsLifecycleComponents()
    {
        var lifecycle = new StubLifecycle();
        var options = new DispatcherOptions("keyvalue");
        options.AddLifecycle("test", lifecycle);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.Status.Should().Be(DispatcherStatus.Created);

        var ct = TestContext.Current.CancellationToken;

        await dispatcher.StartAsyncChecked(ct);

        dispatcher.Status.Should().Be(DispatcherStatus.Running);
        lifecycle.StartCalls.Should().Be(1);
        lifecycle.StopCalls.Should().Be(0);

        await dispatcher.StopAsyncChecked(ct);

        dispatcher.Status.Should().Be(DispatcherStatus.Stopped);
        lifecycle.StopCalls.Should().Be(1);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Register_DuplicateProcedureReportsFailure()
    {
        var options = new DispatcherOptions("payments");
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        var spec = CreateUnaryProcedure("payments", "charge");

        dispatcher.Register(spec).ValueOrChecked();

        var duplicate = dispatcher.Register(spec);

        duplicate.IsFailure.Should().BeTrue();
        OmniRelayErrorAdapter.ToStatus(duplicate.Error!).Should().Be(OmniRelayStatusCode.FailedPrecondition);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Register_WithDifferentServiceReturnsError()
    {
        var options = new DispatcherOptions("catalog");
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        var spec = CreateUnaryProcedure("inventory", "list");

        var result = dispatcher.Register(spec);

        result.IsFailure.Should().BeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).Should().Be(OmniRelayStatusCode.InvalidArgument);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask InvokeUnaryAsync_ResolvesProcedureAliases()
    {
        var options = new DispatcherOptions("keyvalue");
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
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
            aliases: ["v1::user::*", "users::get"]);

        dispatcher.Register(spec).ValueOrChecked();

        var meta = new RequestMeta(service: "keyvalue", procedure: "v1::user::get", transport: "test");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var ct = TestContext.Current.CancellationToken;

        var result = await dispatcher.InvokeUnaryAsync("v1::user::get", request, ct);

        result.IsSuccess.Should().BeTrue();
        callCount.Should().Be(1);

        var snapshot = dispatcher.Introspect();
        var descriptor = snapshot.Procedures.Unary.Should().ContainSingle().Which;
        descriptor.Aliases.Should().Contain("v1::user::*");
        descriptor.Aliases.Should().Contain("users::get");

        var aliasMeta = new RequestMeta(service: "keyvalue", procedure: "users::get", transport: "test");
        var aliasRequest = new Request<ReadOnlyMemory<byte>>(aliasMeta, ReadOnlyMemory<byte>.Empty);

        var aliasResult = await dispatcher.InvokeUnaryAsync("users::get", aliasRequest, ct);

        aliasResult.IsSuccess.Should().BeTrue();
        callCount.Should().Be(2);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask RegisterUnary_BuilderConfiguresPipelineAndMetadata()
    {
        var order = new List<string>();
        var options = new DispatcherOptions("keyvalue");
        options.UnaryInboundMiddleware.Add(new RecordingUnaryInboundMiddleware("global", order));

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

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
                .Use(middleware2)).ValueOrChecked();

        var meta = new RequestMeta(service: "keyvalue", procedure: "user::get", transport: "test");
        var request = new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty);
        var result = await dispatcher.InvokeUnaryAsync("user::get", request, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        order.Should().Equal("global", "m1", "m2", "handler");

        dispatcher.TryGetProcedure("user::get", ProcedureKind.Unary, out var spec).Should().BeTrue();
        var unarySpec = spec.Should().BeOfType<UnaryProcedureSpec>().Which;
        unarySpec.Encoding.Should().Be("json");
        unarySpec.Aliases.Should().Contain("users::get");
        unarySpec.Middleware.Should().HaveCount(2);
        unarySpec.Middleware[0].Should().BeSameAs(middleware1);
        unarySpec.Middleware[1].Should().BeSameAs(middleware2);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask RegisterUnary_WildcardAliasRoutesRequests()
    {
        var options = new DispatcherOptions("catalog");
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        var wildcardInvocations = 0;
        var directInvocations = 0;

        dispatcher.RegisterUnary(
            "catalog::primary",
            (request, _) =>
            {
                wildcardInvocations++;
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            },
            builder => builder.AddAlias("catalog::*")).ValueOrChecked();

        dispatcher.RegisterUnary(
            "catalog::exact",
            (request, _) =>
            {
                directInvocations++;
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            }).ValueOrChecked();

        var wildcardRequest = new Request<ReadOnlyMemory<byte>>(
            new RequestMeta(service: "catalog", procedure: "catalog::listing", transport: "test"),
            ReadOnlyMemory<byte>.Empty);

        var wildcardResult = await dispatcher.InvokeUnaryAsync("catalog::listing", wildcardRequest, TestContext.Current.CancellationToken);
        wildcardResult.IsSuccess.Should().BeTrue(wildcardResult.Error?.ToString());
        wildcardInvocations.Should().Be(1);
        directInvocations.Should().Be(0);

        var directRequest = new Request<ReadOnlyMemory<byte>>(
            new RequestMeta(service: "catalog", procedure: "catalog::exact", transport: "test"),
            ReadOnlyMemory<byte>.Empty);

        var directResult = await dispatcher.InvokeUnaryAsync("catalog::exact", directRequest, TestContext.Current.CancellationToken);
        directResult.IsSuccess.Should().BeTrue(directResult.Error?.ToString());
        directInvocations.Should().Be(1);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask RegisterUnary_WildcardSpecificityPrefersMostSpecificAlias()
    {
        var options = new DispatcherOptions("inventory");
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        var generalCount = 0;
        var versionCount = 0;

        dispatcher.RegisterUnary(
            "inventory::fallback",
            (request, _) =>
            {
                generalCount++;
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            },
            builder => builder.AddAlias("inventory::*")).ValueOrChecked();

        dispatcher.RegisterUnary(
            "inventory::v2::handler",
            (request, _) =>
            {
                versionCount++;
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            },
            builder => builder.AddAlias("inventory::v2::*")).ValueOrChecked();

        var request = new Request<ReadOnlyMemory<byte>>(
            new RequestMeta(service: "inventory", procedure: "inventory::v2::list", transport: "test"),
            ReadOnlyMemory<byte>.Empty);

        var result = await dispatcher.InvokeUnaryAsync("inventory::v2::list", request, TestContext.Current.CancellationToken);
        result.IsSuccess.Should().BeTrue(result.Error?.ToString());
        generalCount.Should().Be(0);
        versionCount.Should().Be(1);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void RegisterUnary_DuplicateWildcardAliasReturnsError()
    {
        var options = new DispatcherOptions("billing");
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.RegisterUnary(
            "billing::primary",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))),
            builder => builder.AddAlias("billing::*")).ValueOrChecked();

        var conflict = dispatcher.RegisterUnary(
            "billing::secondary",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))),
            builder => builder.AddAlias("billing::*"));

        conflict.IsFailure.Should().BeTrue();
        conflict.Error?.Message.Should().ContainEquivalentOf("conflicts");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void RegisterUnary_BuilderRequiresHandler()
    {
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(new DispatcherOptions("edge"));

        var result = dispatcher.RegisterUnary("missing", builder => builder.WithEncoding("json"));

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("dispatcher.procedure.handler_missing");
        result.Error.TryGetMetadata("procedure", out string? procedure).Should().BeTrue();
        procedure.Should().Be("missing");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void RegisterUnary_BlankName_ReturnsInvalidArgument()
    {
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(new DispatcherOptions("svc"));

        var result = dispatcher.RegisterUnary(
            "   ",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))));

        result.IsFailure.Should().BeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).Should().Be(OmniRelayStatusCode.InvalidArgument);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void RegisterUnary_TrimsName()
    {
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(new DispatcherOptions("svc"));

        dispatcher.RegisterUnary(" svc::call  ", (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)))).ValueOrChecked();

        dispatcher.TryGetProcedure("svc::call", ProcedureKind.Unary, out _).Should().BeTrue();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void RegisterStream_BuilderConfiguresMetadata()
    {
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(new DispatcherOptions("streaming"));
        var metadata = new StreamIntrospectionMetadata(
            new StreamChannelMetadata(StreamDirection.Server, "bounded-channel", Capacity: 32, TracksMessageCount: true));

        dispatcher.RegisterStream(
            "events::subscribe",
            (request, options, cancellationToken) =>
                ValueTask.FromResult(Err<IStreamCall>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unimplemented, "stub"))),
            builder => builder
                .WithEncoding("json")
                .AddAliases(["events::watch"])
                .WithMetadata(metadata)).ValueOrChecked();

        dispatcher.TryGetProcedure("events::subscribe", ProcedureKind.Stream, out var spec).Should().BeTrue();
        var streamSpec = spec.Should().BeOfType<StreamProcedureSpec>().Which;
        streamSpec.Encoding.Should().Be("json");
        streamSpec.Metadata.Should().Be(metadata);
        streamSpec.Aliases.Should().Contain("events::watch");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ClientConfig_ReturnsOutboundsAndMiddleware()
    {
        var unaryOutbound = new StubUnaryOutbound();
        var unaryMiddleware = new PassthroughUnaryOutboundMiddleware();

        var options = new DispatcherOptions("frontend");
        options.AddUnaryOutbound("backend", null, unaryOutbound);
        options.UnaryOutboundMiddleware.Add(unaryMiddleware);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        var configResult = dispatcher.ClientConfig("backend");
        configResult.IsSuccess.Should().BeTrue(configResult.Error?.ToString());
        var config = configResult.Value;

        config.Service.Should().Be("backend");
        config.TryGetUnary(null, out var resolved).Should().BeTrue();
        resolved.Should().BeSameAs(unaryOutbound);
        config.Unary.Values.Should().Contain(unaryOutbound);
        config.UnaryMiddleware.Should().Contain(unaryMiddleware);
        config.ClientStream.Should().BeEmpty();
        config.Duplex.Should().BeEmpty();
        config.ClientStreamMiddleware.Should().BeEmpty();
        config.DuplexMiddleware.Should().BeEmpty();
        config.ClientStreamMiddleware.Should().BeEmpty();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ClientConfig_UnknownServiceReturnsError()
    {
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(new DispatcherOptions("frontend"));

        var config = dispatcher.ClientConfig("missing");

        config.IsFailure.Should().BeTrue();
        OmniRelayErrorAdapter.ToStatus(config.Error!).Should().Be(OmniRelayStatusCode.NotFound);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask InvokeClientStreamAsync_ProcessesRequestAndCompletesResponse()
    {
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(new DispatcherOptions("keyvalue"));

        dispatcher.Register(new ClientStreamProcedureSpec(
            "keyvalue",
            "aggregate",
            async (context, cancellationToken) =>
            {
                var totalBytes = 0;
                await foreach (var payload in context.Requests.ReadAllAsync(cancellationToken))
                {
                    totalBytes += payload.Length;
                }

                var responseMeta = new ResponseMeta(encoding: "application/octet-stream");
                var response = Response<ReadOnlyMemory<byte>>.Create(BitConverter.GetBytes(totalBytes), responseMeta);
                return Ok(response);
            })).ValueOrChecked();

        var requestMeta = new RequestMeta(service: "keyvalue", procedure: "aggregate", transport: "test");
        var ct = TestContext.Current.CancellationToken;
        var result = await dispatcher.InvokeClientStreamAsync("aggregate", requestMeta, ct);

        result.IsSuccess.Should().BeTrue();

        await using var call = result.Value;

        await call.Requests.WriteAsync(new byte[] { 0x01, 0x02 }, ct);
        await call.Requests.WriteAsync(new byte[] { 0x03 }, ct);
        await call.CompleteWriterAsync(cancellationToken: ct);

        var responseResult = await call.Response;

        responseResult.IsSuccess.Should().BeTrue();
        call.ResponseMeta.Encoding.Should().Be("application/octet-stream");
        var count = BitConverter.ToInt32(responseResult.Value.Body.Span);
        count.Should().Be(3);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask InvokeClientStreamAsync_WhenProcedureMissingReturnsError()
    {
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(new DispatcherOptions("keyvalue"));
        var requestMeta = new RequestMeta(service: "keyvalue", procedure: "missing", transport: "test");
        var ct = TestContext.Current.CancellationToken;

        var result = await dispatcher.InvokeClientStreamAsync("missing", requestMeta, ct);

        result.IsFailure.Should().BeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).Should().Be(OmniRelayStatusCode.Unimplemented);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Introspect_ReportsCurrentState()
    {
        var lifecycle = new StubLifecycle();
        var unaryInbound = new PassthroughUnaryInboundMiddleware();
        var unaryOutbound = new PassthroughUnaryOutboundMiddleware();
        var options = new DispatcherOptions("keyvalue");
        options.AddLifecycle("test", lifecycle);
        options.UnaryInboundMiddleware.Add(unaryInbound);
        options.UnaryOutboundMiddleware.Add(unaryOutbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(CreateUnaryProcedure("keyvalue", "get")).ValueOrChecked();

        var beforeStart = dispatcher.Introspect();
        beforeStart.Status.Should().Be(DispatcherStatus.Created);
        beforeStart.Procedures.Unary.Should().HaveCount(1);
        beforeStart.Procedures.Unary[0].Name.Should().Be("get");
        beforeStart.Middleware.InboundClientStream.Should().BeEmpty();
        beforeStart.Middleware.OutboundClientStream.Should().BeEmpty();
        beforeStart.Middleware.InboundDuplex.Should().BeEmpty();
        beforeStart.Middleware.OutboundDuplex.Should().BeEmpty();

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsyncChecked(ct);

        var snapshot = dispatcher.Introspect();

        snapshot.Service.Should().Be("keyvalue");
        snapshot.Status.Should().Be(DispatcherStatus.Running);
        snapshot.Components.Should().Contain(component => component.Name == "test");
        snapshot.Middleware.InboundUnary.Should().Contain(typeName => typeName.Contains(nameof(PassthroughUnaryInboundMiddleware), StringComparison.Ordinal));
        snapshot.Middleware.OutboundUnary.Should().Contain(typeName => typeName.Contains(nameof(PassthroughUnaryOutboundMiddleware), StringComparison.Ordinal));
        snapshot.Middleware.InboundClientStream.Should().BeEmpty();
        snapshot.Middleware.OutboundClientStream.Should().BeEmpty();
        snapshot.Middleware.InboundDuplex.Should().BeEmpty();
        snapshot.Middleware.OutboundDuplex.Should().BeEmpty();

        await dispatcher.StopAsyncChecked(ct);

        var afterStop = dispatcher.Introspect();
        afterStop.Status.Should().Be(DispatcherStatus.Stopped);
        afterStop.Middleware.InboundClientStream.Should().BeEmpty();
        afterStop.Middleware.OutboundClientStream.Should().BeEmpty();
        afterStop.Middleware.InboundDuplex.Should().BeEmpty();
        afterStop.Middleware.OutboundDuplex.Should().BeEmpty();
        afterStop.Middleware.OutboundClientStream.Should().BeEmpty();
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
        private int _startCalls;
        private int _stopCalls;

        public int StartCalls => _startCalls;

        public int StopCalls => _stopCalls;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startCalls);
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _stopCalls);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubUnaryOutbound : IUnaryOutbound
    {
        private int _startCalls;
        private int _stopCalls;

        public int StartCalls => _startCalls;
        public int StopCalls => _stopCalls;

        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "not-implemented")));

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startCalls);
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _stopCalls);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PassthroughUnaryOutboundMiddleware : IUnaryOutboundMiddleware
    {
        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken,
            UnaryOutboundHandler next) => next(request, cancellationToken);
    }

    private sealed class PassthroughUnaryInboundMiddleware : IUnaryInboundMiddleware
    {
        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken,
            UnaryInboundHandler next) => next(request, cancellationToken);
    }

    private sealed class RecordingUnaryInboundMiddleware(string name, IList<string> order) : IUnaryInboundMiddleware
    {
        private readonly string _name = name;
        private readonly IList<string> _order = order ?? throw new ArgumentNullException(nameof(order));

        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken,
            UnaryInboundHandler next)
        {
            _order.Add(_name);
            return next(request, cancellationToken);
        }
    }
}
