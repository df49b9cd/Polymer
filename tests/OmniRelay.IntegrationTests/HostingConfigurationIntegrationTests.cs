using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Hugo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OmniRelay.Configuration;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Tests;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.IntegrationTests;

public class HostingConfigurationIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async Task AddOmniRelayDispatcher_ComposesConfigurationFromJsonAndEnvironment()
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
        builder.Services.AddOmniRelayDispatcher(builder.Configuration.GetSection("omnirelay"));

        using var host = builder.Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher.Dispatcher>();

        Assert.Equal("env-service", dispatcher.ServiceName);

        var hostedServices = host.Services.GetServices<IHostedService>().ToList();
        Assert.Contains(hostedServices, service => service.GetType().Name == "DispatcherHostedService");

        var introspection = dispatcher.Introspect();
        Assert.Contains(introspection.Components, component =>
            component.Name == "http-env" &&
            component.ComponentType.Contains("HttpInbound", StringComparison.Ordinal));

        var ledgerOutbound = Assert.Single(introspection.Outbounds, outbound => outbound.Service == "ledger");
        var unaryOutbound = Assert.Single(ledgerOutbound.Unary);
        Assert.Equal("primary", unaryOutbound.Key);
        Assert.Contains("HttpOutbound", unaryOutbound.ImplementationType, StringComparison.Ordinal);

        var clientConfig = dispatcher.ClientConfig("ledger");
        Assert.True(clientConfig.TryGetUnary("primary", out var outboundInstance));
        Assert.IsAssignableFrom<HttpOutbound>(outboundInstance);

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
        builder.Services.AddOmniRelayDispatcher(builder.Configuration.GetSection("omnirelay"));

        using var host = builder.Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher.Dispatcher>();

        Assert.Equal("cli-service", dispatcher.ServiceName);
        var components = dispatcher.Introspect().Components;
        Assert.Contains(components, component => component.Name == "http-cli");

        var paymentsOutbound = Assert.Single(dispatcher.Introspect().Outbounds, outbound => outbound.Service == "payments");
        var unaryBinding = Assert.Single(paymentsOutbound.Unary);
        Assert.Contains("HttpOutbound", unaryBinding.ImplementationType, StringComparison.Ordinal);

        var clientConfig = dispatcher.ClientConfig("payments");
        Assert.True(clientConfig.TryGetUnary("primary", out var outboundInstance));
        Assert.IsAssignableFrom<HttpOutbound>(outboundInstance);
    }

    [Fact(Timeout = 30_000)]
    public async Task AddOmniRelayDispatcher_StartsMultipleInboundsAndExposesMetadata()
    {
        var httpPort = TestPortAllocator.GetRandomPort();
        var grpcPort = TestPortAllocator.GetRandomPort();
        var inboundSpec = new RecordingInboundSpec();

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "multi-inbounds",
            ["omnirelay:inbounds:http:0:name"] = "http-primary",
            ["omnirelay:inbounds:http:0:urls:0"] = $"http://127.0.0.1:{httpPort}/",
            ["omnirelay:inbounds:grpc:0:name"] = "grpc-primary",
            ["omnirelay:inbounds:grpc:0:urls:0"] = $"http://127.0.0.1:{grpcPort}",
            ["omnirelay:inbounds:custom:0:spec"] = RecordingInboundSpec.SpecName,
            ["omnirelay:inbounds:custom:0:name"] = "custom-primary",
            ["omnirelay:inbounds:custom:0:endpoint"] = "/ws"
        });

        builder.Services.AddLogging();
        builder.Services.AddSingleton<ICustomInboundSpec>(inboundSpec);
        builder.Services.AddOmniRelayDispatcher(builder.Configuration.GetSection("omnirelay"));

        using var host = builder.Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher.Dispatcher>();

        var ct = TestContext.Current.CancellationToken;
        await host.StartAsync(ct);
        await host.StopAsync(CancellationToken.None);

        var components = dispatcher.Introspect().Components;
        Assert.Contains(components, component => component.Name == "http-primary" && component.ComponentType.Contains("HttpInbound", StringComparison.Ordinal));
        Assert.Contains(components, component => component.Name == "grpc-primary" && component.ComponentType.Contains("GrpcInbound", StringComparison.Ordinal));
        Assert.Contains(components, component => component.Name == "custom-primary" && component.ComponentType.Contains(nameof(RecordingInboundLifecycle), StringComparison.Ordinal));

        var customLifecycle = inboundSpec.Created.Single(lifecycle => lifecycle.Name == "custom-primary");
        Assert.True(customLifecycle.Started);
        Assert.True(customLifecycle.Stopped);
        Assert.NotNull(customLifecycle.Dispatcher);
    }

    [Fact(Timeout = 30_000)]
    public void AddOmniRelayDispatcher_UsesCustomTransportSpecs()
    {
        var inboundSpec = new RecordingInboundSpec();
        var outboundSpec = new RecordingOutboundSpec();

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "custom-spec",
            ["omnirelay:inbounds:custom:0:spec"] = RecordingInboundSpec.SpecName,
            ["omnirelay:inbounds:custom:0:name"] = "ws-inbound",
            ["omnirelay:inbounds:custom:0:endpoint"] = "/ws",
            ["omnirelay:outbounds:search:unary:custom:0:spec"] = RecordingOutboundSpec.SpecName,
            ["omnirelay:outbounds:search:unary:custom:0:key"] = "primary",
            ["omnirelay:outbounds:search:unary:custom:0:url"] = "http://search.internal:8080"
        });

        builder.Services.AddLogging();
        builder.Services.AddSingleton<ICustomInboundSpec>(inboundSpec);
        builder.Services.AddSingleton<ICustomOutboundSpec>(outboundSpec);
        builder.Services.AddOmniRelayDispatcher(builder.Configuration.GetSection("omnirelay"));

        using var host = builder.Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher.Dispatcher>();

        Assert.Equal("/ws", inboundSpec.LastEndpoint);
        Assert.Equal("http://search.internal:8080", outboundSpec.LastUrl);

        var introspection = dispatcher.Introspect();
        Assert.Contains(introspection.Components, component => component.Name == "ws-inbound");

        var searchOutbounds = Assert.Single(introspection.Outbounds, outbound => outbound.Service == "search");
        var unary = Assert.Single(searchOutbounds.Unary);
        Assert.Equal("primary", unary.Key);
        Assert.Contains(nameof(RecordingUnaryOutbound), unary.ImplementationType, StringComparison.Ordinal);

        var clientConfig = dispatcher.ClientConfig("search");
        Assert.True(clientConfig.TryGetUnary("primary", out var outboundInstance));
        Assert.IsType<RecordingUnaryOutbound>(outboundInstance);
    }

    private sealed class RecordingInboundSpec : ICustomInboundSpec
    {
        public const string SpecName = "test-inbound";
        public string Name => SpecName;
        public string? LastEndpoint { get; private set; }
        public List<RecordingInboundLifecycle> Created { get; } = new();

        public ILifecycle CreateInbound(IConfigurationSection configuration, IServiceProvider services)
        {
            LastEndpoint = configuration["endpoint"];
            var lifecycle = new RecordingInboundLifecycle(configuration["name"] ?? "custom");
            Created.Add(lifecycle);
            return lifecycle;
        }
    }

    private sealed class RecordingInboundLifecycle(string name) : ILifecycle, IDispatcherAware
    {
        private readonly string _name = name;
        public Dispatcher.Dispatcher? Dispatcher { get; private set; }
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public string Name => _name;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            Started = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            Stopped = true;
            return ValueTask.CompletedTask;
        }

        public void Bind(Dispatcher.Dispatcher dispatcher) => Dispatcher = dispatcher;

        public override string ToString() => _name;
    }

    private sealed class RecordingOutboundSpec : ICustomOutboundSpec
    {
        public const string SpecName = "test-outbound";
        public string Name => SpecName;
        public string? LastUrl { get; private set; }

        public IUnaryOutbound? CreateUnaryOutbound(IConfigurationSection configuration, IServiceProvider services)
        {
            LastUrl = configuration["url"];
            return new RecordingUnaryOutbound(configuration["key"] ?? "default", LastUrl ?? "missing");
        }
    }

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
}
