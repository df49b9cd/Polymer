using System.Text;
using AwesomeAssertions;
using Hugo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Dispatcher.Config;
using OmniRelay.Errors;
using OmniRelay.IntegrationTests.Codecs;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;
using Xunit;
using static AwesomeAssertions.FluentActions;

namespace OmniRelay.IntegrationTests;

public class HostingConfigurationIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async ValueTask AddOmniRelayDispatcher_ComposesConfigurationFromJsonAndEnvironment()
    {
        var port = TestPortAllocator.GetRandomPort();
        var json = $$"""
        {
          "omnirelay": {
            "service": "json-service",
            "inbounds": {
              "http": [{
                "name": "http-json",
                "urls": ["http://127.0.0.1:{{port}}/"]
              }]
            },
            "outbounds": {
              "ledger": {
                "unary": {
                  "http": [{
                    "key": "primary",
                    "url": "http://ledger-json:8080"
                  }]
                }
              }
            }
          }
        }
        """;

        var builder = Host.CreateApplicationBuilder();
        using var jsonStream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        builder.Configuration.AddJsonStream(jsonStream);

        var overlay = new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "env-service",
            ["omnirelay:inbounds:http:0:name"] = "http-env",
            ["omnirelay:outbounds:ledger:unary:http:0:url"] = "http://ledger-env:9090",
            ["omnirelay:logging:level"] = "Trace"
        };
        builder.Configuration.AddInMemoryCollection(overlay);

        builder.Services.AddLogging();
        builder.Services.AddOmniRelayDispatcherFromConfiguration(builder.Configuration.GetSection("omnirelay"));

        using var host = builder.Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher.Dispatcher>();

        dispatcher.ServiceName.Should().Be("env-service");

        var hostedServices = host.Services.GetServices<IHostedService>().ToList();
        hostedServices.Should().Contain(service => service.GetType().Name == "DispatcherHostedService");

        var introspection = dispatcher.Introspect();
        introspection.Components.Should().Contain(component =>
            component.Name == "http-env" &&
            component.ComponentType.Contains("HttpInbound", StringComparison.Ordinal));

        var ledgerOutbound = introspection.Outbounds.Should().ContainSingle(outbound => outbound.Service == "ledger").Which;
        var unaryOutbound = ledgerOutbound.Unary.Should().ContainSingle(outbound => outbound.Key == "primary").Which;
        unaryOutbound.Key.Should().Be("primary");
        unaryOutbound.ImplementationType.Should().Contain("HttpOutbound");

        var clientConfig = dispatcher.ClientConfigChecked("ledger");
        clientConfig.TryGetUnary("primary", out var outboundInstance).Should().BeTrue();
        outboundInstance.Should().BeAssignableTo<HttpOutbound>();

        var ct = TestContext.Current.CancellationToken;
        await host.StartAsync(ct);
        await host.StopAsync(CancellationToken.None);
    }

    [Fact(Timeout = 30_000)]
    public void AddOmniRelayDispatcher_HonorsCommandLineOverrides()
    {
        var port = TestPortAllocator.GetRandomPort();
        var json = $$"""
        {
          "omnirelay": {
            "service": "json-service",
            "inbounds": {
              "http": [{
                "name": "http-json",
                "urls": ["http://127.0.0.1:{{port}}/"]
              }]
            },
            "outbounds": {
              "payments": {
                "unary": {
                  "http": [{
                    "key": "primary",
                    "url": "http://json-payments:8080"
                  }]
                }
              }
            }
          }
        }
        """;

        var args = new[]
        {
            "--omnirelay:service=cli-service",
            "--omnirelay:inbounds:http:0:name=http-cli",
            "--omnirelay:outbounds:payments:unary:http:0:url=http://cli-payments:9090"
        };

        var builder = Host.CreateApplicationBuilder();
        using var jsonStream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        builder.Configuration.AddJsonStream(jsonStream);
        builder.Configuration.AddCommandLine(args);

        builder.Services.AddLogging();
        builder.Services.AddOmniRelayDispatcherFromConfiguration(builder.Configuration.GetSection("omnirelay"));

        using var host = builder.Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher.Dispatcher>();

        dispatcher.ServiceName.Should().Be("cli-service");
        var components = dispatcher.Introspect().Components;
        components.Should().Contain(component => component.Name == "http-cli");

        var paymentsOutbound = dispatcher.Introspect().Outbounds.Should().ContainSingle(outbound => outbound.Service == "payments").Which;
        var unaryBinding = paymentsOutbound.Unary.Should().ContainSingle(binding => binding.Key == "primary").Which;
        unaryBinding.ImplementationType.Should().Contain("HttpOutbound");

        var clientConfig = dispatcher.ClientConfigChecked("payments");
        clientConfig.TryGetUnary("primary", out var outboundInstance).Should().BeTrue();
        outboundInstance.Should().BeAssignableTo<HttpOutbound>();
    }

    [Http3Fact(Timeout = 30_000)]
    public async ValueTask AddOmniRelayDispatcher_EnableHttp3WithoutHttps_Throws()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "http3-guard",
            ["omnirelay:inbounds:http:0:name"] = "http3-inbound",
            ["omnirelay:inbounds:http:0:urls:0"] = $"http://127.0.0.1:{TestPortAllocator.GetRandomPort()}/",
            ["omnirelay:inbounds:http:0:runtime:enableHttp3"] = "true"
        });

        builder.Services.AddLogging();
        builder.Services.AddOmniRelayDispatcherFromConfiguration(builder.Configuration.GetSection("omnirelay"));

        using var host = builder.Build();

        var ex = await Invoking(() => host.StartAsync(TestContext.Current.CancellationToken))
            .Should().ThrowAsync<OmniRelayException>();

        ex.Which.StatusCode.Should().BeOneOf(OmniRelayStatusCode.InvalidArgument, OmniRelayStatusCode.Unknown);
        ex.Which.Message.Should().ContainEquivalentOf("HTTP/3 requires HTTPS");
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask AddOmniRelayDispatcher_ConfiguresHttpAndGrpcOutboundsWithMiddleware()
    {
        var inboundPort = TestPortAllocator.GetRandomPort();
        var httpOutboundUrl = $"http://127.0.0.1:{TestPortAllocator.GetRandomPort()}/yarpc";
        var grpcAddress = $"http://127.0.0.1:{TestPortAllocator.GetRandomPort()}";
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "transport-middleware",
            ["omnirelay:inbounds:http:0:name"] = "http-inbound",
            ["omnirelay:inbounds:http:0:urls:0"] = $"http://127.0.0.1:{inboundPort}/",

            ["omnirelay:outbounds:workflow:unary:http:0:key"] = "http-unary",
            ["omnirelay:outbounds:workflow:unary:http:0:url"] = httpOutboundUrl,
            ["omnirelay:outbounds:workflow:oneway:http:0:key"] = "http-oneway",
            ["omnirelay:outbounds:workflow:oneway:http:0:url"] = httpOutboundUrl,

            ["omnirelay:outbounds:workflow:stream:grpc:0:key"] = "grpc-stream",
            ["omnirelay:outbounds:workflow:stream:grpc:0:remoteService"] = "workflow",
            ["omnirelay:outbounds:workflow:stream:grpc:0:addresses:0"] = grpcAddress,

            ["omnirelay:outbounds:workflow:clientStream:grpc:0:key"] = "grpc-client",
            ["omnirelay:outbounds:workflow:clientStream:grpc:0:remoteService"] = "workflow",
            ["omnirelay:outbounds:workflow:clientStream:grpc:0:addresses:0"] = grpcAddress,

            ["omnirelay:outbounds:workflow:duplex:grpc:0:key"] = "grpc-duplex",
            ["omnirelay:outbounds:workflow:duplex:grpc:0:remoteService"] = "workflow",
            ["omnirelay:outbounds:workflow:duplex:grpc:0:addresses:0"] = grpcAddress,

            ["omnirelay:middleware:outbound:unary:0"] = "tracing",
            ["omnirelay:middleware:outbound:unary:1"] = "metrics",
            ["omnirelay:middleware:outbound:oneway:0"] = "tracing",
            ["omnirelay:middleware:outbound:stream:0"] = "tracing",
            ["omnirelay:middleware:outbound:clientStream:0"] = "tracing",
            ["omnirelay:middleware:outbound:duplex:0"] = "tracing"
        });

        builder.Services.AddLogging();
        builder.Services.AddOmniRelayDispatcherFromConfiguration(
            builder.Configuration.GetSection("omnirelay"),
            registerComponents: registry =>
            {
                registry.RegisterOutboundMiddlewareType<RpcTracingMiddleware>("tracing");
                registry.RegisterOutboundMiddlewareType<RpcMetricsMiddleware>("metrics");
            });

        using var host = builder.Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher.Dispatcher>();

        // Diagnostic: ensure config binding brought in outbounds
        var ct = TestContext.Current.CancellationToken;
        await host.StartAsync(ct);
        await host.StopAsync(CancellationToken.None);

        var clientConfig = dispatcher.ClientConfigChecked("workflow");
        clientConfig.Unary["http-unary"].Should().BeOfType<HttpOutbound>();
        clientConfig.Oneway["http-oneway"].Should().BeOfType<HttpOutbound>();
        clientConfig.Stream["grpc-stream"].Should().BeOfType<GrpcOutbound>();
        clientConfig.ClientStream["grpc-client"].Should().BeOfType<GrpcOutbound>();
        clientConfig.Duplex["grpc-duplex"].Should().BeOfType<GrpcOutbound>();

        clientConfig.UnaryMiddleware.Should().Contain(middleware => middleware is RpcTracingMiddleware);
        clientConfig.UnaryMiddleware.Should().Contain(middleware => middleware is RpcMetricsMiddleware);
        clientConfig.OnewayMiddleware.Should().Contain(middleware => middleware is RpcTracingMiddleware);
        clientConfig.StreamMiddleware.Should().Contain(middleware => middleware is RpcTracingMiddleware);
        clientConfig.ClientStreamMiddleware.Should().Contain(middleware => middleware is RpcTracingMiddleware);
        clientConfig.DuplexMiddleware.Should().Contain(middleware => middleware is RpcTracingMiddleware);

        var outboundDescriptor = dispatcher.Introspect().Outbounds.Should().ContainSingle(o => o.Service == "workflow").Which;
        outboundDescriptor.Unary.Should().Contain(descriptor =>
            descriptor.Key == "http-unary" && descriptor.ImplementationType.Contains(nameof(HttpOutbound), StringComparison.Ordinal));
        outboundDescriptor.Oneway.Should().Contain(descriptor =>
            descriptor.Key == "http-oneway" && descriptor.ImplementationType.Contains(nameof(HttpOutbound), StringComparison.Ordinal));
        outboundDescriptor.Stream.Should().Contain(descriptor =>
            descriptor.Key == "grpc-stream" && descriptor.ImplementationType.Contains(nameof(GrpcOutbound), StringComparison.Ordinal));
        outboundDescriptor.ClientStream.Should().Contain(descriptor =>
            descriptor.Key == "grpc-client" && descriptor.ImplementationType.Contains(nameof(GrpcOutbound), StringComparison.Ordinal));
        outboundDescriptor.Duplex.Should().Contain(descriptor =>
            descriptor.Key == "grpc-duplex" && descriptor.ImplementationType.Contains(nameof(GrpcOutbound), StringComparison.Ordinal));
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask AddOmniRelayDispatcher_RegistersJsonCodecProfileAndMiddleware()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "codec-flags",
            ["omnirelay:inbounds:http:0:name"] = "http",
            ["omnirelay:inbounds:http:0:urls:0"] = $"http://127.0.0.1:{TestPortAllocator.GetRandomPort()}/",
            ["omnirelay:outbounds:codec:unary:http:0:key"] = "primary",
            ["omnirelay:outbounds:codec:unary:http:0:url"] = $"http://127.0.0.1:{TestPortAllocator.GetRandomPort()}/",
            ["omnirelay:middleware:outbound:unary:0"] = "tracing",
            ["omnirelay:middleware:inbound:unary:0"] = "metrics",
            ["omnirelay:encodings:json:profiles:pretty:options:writeIndented"] = "true",
            ["omnirelay:encodings:json:outbound:0:service"] = "codec",
            ["omnirelay:encodings:json:outbound:0:procedure"] = "feature::echo",
            ["omnirelay:encodings:json:outbound:0:kind"] = "Unary",
            ["omnirelay:encodings:json:outbound:0:profile"] = "pretty",
            ["omnirelay:encodings:json:outbound:0:encoding"] = "application/json;profile=pretty",
            ["omnirelay:encodings:json:outbound:0:codecKey"] = "json-codec"
        });

        builder.Services.AddLogging();
        builder.Services.AddOmniRelayDispatcherFromConfiguration(
            builder.Configuration.GetSection("omnirelay"),
            registerComponents: registry =>
            {
                registry.RegisterOutboundMiddlewareType<RpcTracingMiddleware>("tracing");
                registry.RegisterInboundMiddlewareType<RpcMetricsMiddleware>("metrics");
                registry.RegisterOutboundJsonCodec<JsonCodecRequest, JsonCodecResponse>(
                    "json-codec",
                    "application/json;profile=pretty");
            });

        using var host = builder.Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher.Dispatcher>();

        var ct = TestContext.Current.CancellationToken;
        await host.StartAsync(ct);
        await host.StopAsync(CancellationToken.None);

        dispatcher.UnaryOutboundMiddleware.Should().Contain(middleware => middleware is RpcTracingMiddleware);
        dispatcher.UnaryInboundMiddleware.Should().Contain(middleware => middleware is RpcMetricsMiddleware);

        var codecs = dispatcher.Codecs.Snapshot();
        codecs.Should().Contain(entry =>
            entry.Scope == ProcedureCodecScope.Outbound &&
            string.Equals(entry.Service, "codec", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.Procedure, "feature::echo", StringComparison.OrdinalIgnoreCase) &&
            entry.Kind == ProcedureKind.Unary &&
            entry.Descriptor.RequestType == typeof(JsonCodecRequest) &&
            entry.Descriptor.ResponseType == typeof(JsonCodecResponse) &&
            entry.Descriptor.Encoding == "application/json;profile=pretty");
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask AddOmniRelayDispatcher_StartsMultipleInboundsAndExposesMetadata()
    {
        var httpPort = TestPortAllocator.GetRandomPort();
        var grpcPort = TestPortAllocator.GetRandomPort();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "multi-inbounds",
            ["omnirelay:inbounds:http:0:name"] = "http-primary",
            ["omnirelay:inbounds:http:0:urls:0"] = $"http://127.0.0.1:{httpPort}/",
            ["omnirelay:inbounds:grpc:0:name"] = "grpc-primary",
            ["omnirelay:inbounds:grpc:0:urls:0"] = $"http://127.0.0.1:{grpcPort}"
        });

        builder.Services.AddLogging();
        builder.Services.AddOmniRelayDispatcherFromConfiguration(builder.Configuration.GetSection("omnirelay"));

        using var host = builder.Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher.Dispatcher>();

        var ct = TestContext.Current.CancellationToken;
        await host.StartAsync(ct);
        await host.StopAsync(CancellationToken.None);

        var components = dispatcher.Introspect().Components;
        components.Should().Contain(component => component.Name == "http-primary" && component.ComponentType.Contains("HttpInbound", StringComparison.Ordinal));
        components.Should().Contain(component => component.Name == "grpc-primary" && component.ComponentType.Contains("GrpcInbound", StringComparison.Ordinal));
    }

    // Custom transport spec support was removed with the legacy configuration binder. Test omitted.

    private sealed class RecordingUnaryOutbound(string key, string url) : IUnaryOutbound
    {
        public string Key { get; } = key;
        public string Url { get; } = url;

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Recording outbound should not be invoked in this test.");

        public override string ToString() => $"{Key}:{Url}";
    }

    private sealed class RecordingOnewayOutbound(string key, string url) : IOnewayOutbound
    {
        public string Key { get; } = key;
        public string Url { get; } = url;

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<Result<OnewayAck>> CallAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Recording outbound should not be invoked in this test.");

        public override string ToString() => $"{Key}:{Url}";
    }

    private sealed class RecordingStreamOutbound(string key, string url) : IStreamOutbound
    {
        public string Key { get; } = key;
        public string Url { get; } = url;

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<Result<IStreamCall>> CallAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            StreamCallOptions options,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Recording outbound should not be invoked in this test.");

        public override string ToString() => $"{Key}:{Url}";
    }

    private sealed class RecordingClientStreamOutbound(string key, string url) : IClientStreamOutbound
    {
        public string Key { get; } = key;
        public string Url { get; } = url;

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<Result<IClientStreamTransportCall>> CallAsync(
            RequestMeta requestMeta,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Recording outbound should not be invoked in this test.");

        public override string ToString() => $"{Key}:{Url}";
    }

    private sealed class RecordingDuplexOutbound(string key, string url) : IDuplexOutbound
    {
        public string Key { get; } = key;
        public string Url { get; } = url;

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<Result<IDuplexStreamCall>> CallAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Recording outbound should not be invoked in this test.");

        public override string ToString() => $"{Key}:{Url}";
    }
}
