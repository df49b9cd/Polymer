using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Hugo;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polymer.Configuration;
using Polymer.Core;
using Polymer.Core.Peers;
using Polymer.Core.Transport;
using Polymer.Dispatcher;
using Polymer.Errors;
using Xunit;
using static Hugo.Go;
using PolymerDispatcher = Polymer.Dispatcher.Dispatcher;

namespace Polymer.Tests.Configuration;

public class PolymerConfigurationTests
{
    [Fact]
    public void AddPolymerDispatcher_BuildsDispatcherFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["polymer:service"] = "gateway",
                ["polymer:inbounds:http:0:urls:0"] = "http://127.0.0.1:8080",
                ["polymer:outbounds:keyvalue:unary:http:0:key"] = "primary",
                ["polymer:outbounds:keyvalue:unary:http:0:url"] = "http://127.0.0.1:8081",
                ["polymer:outbounds:keyvalue:oneway:http:0:key"] = "primary",
                ["polymer:outbounds:keyvalue:oneway:http:0:url"] = "http://127.0.0.1:8081",
                ["polymer:logging:level"] = "Warning",
                ["polymer:logging:overrides:Polymer.Transport.Http"] = "Trace"
            }!)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPolymerDispatcher(configuration.GetSection("polymer"));

        using var provider = services.BuildServiceProvider();

        var dispatcher = provider.GetRequiredService<PolymerDispatcher>();
        Assert.Equal("gateway", dispatcher.ServiceName);

        var clientConfig = dispatcher.ClientConfig("keyvalue");
        Assert.True(clientConfig.TryGetUnary("primary", out var unary));
        Assert.NotNull(unary);
        Assert.True(clientConfig.TryGetOneway("primary", out var oneway));
        Assert.NotNull(oneway);

        var loggerOptions = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value;
        Assert.Equal(LogLevel.Warning, loggerOptions.MinLevel);
        Assert.Contains(
            loggerOptions.Rules,
            rule => string.Equals(rule.CategoryName, "Polymer.Transport.Http", StringComparison.Ordinal) &&
                    rule.LogLevel == LogLevel.Trace);

        var hostedServices = provider.GetServices<IHostedService>().ToList();
        Assert.Single(hostedServices);
    }

    [Fact]
    public void AddPolymerDispatcher_MissingServiceThrows()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();

        Assert.Throws<PolymerConfigurationException>(
            () => services.AddPolymerDispatcher(configuration.GetSection("polymer")));
    }

    [Fact]
    public void AddPolymerDispatcher_InvalidPeerChooserThrows()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["polymer:service"] = "edge",
                ["polymer:outbounds:inventory:stream:grpc:0:addresses:0"] = "http://127.0.0.1:9090",
                ["polymer:outbounds:inventory:stream:grpc:0:peerChooser"] = "random-weighted"
            }!)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPolymerDispatcher(configuration.GetSection("polymer"));

        using var provider = services.BuildServiceProvider();
        Assert.Throws<PolymerConfigurationException>(() => provider.GetRequiredService<PolymerDispatcher>());
    }

    [Fact]
    public void AddPolymerDispatcher_UsesCustomTransportSpecs()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["polymer:service"] = "custom",
                ["polymer:inbounds:custom:0:spec"] = TestInboundSpec.SpecName,
                ["polymer:inbounds:custom:0:name"] = "ws-inbound",
                ["polymer:inbounds:custom:0:endpoint"] = "/ws",
                ["polymer:outbounds:search:unary:custom:0:spec"] = TestOutboundSpec.SpecName,
                ["polymer:outbounds:search:unary:custom:0:key"] = "primary",
                ["polymer:outbounds:search:unary:custom:0:url"] = "http://search.internal:8080"
            }!)
            .Build();

        var inboundSpec = new TestInboundSpec();
        var outboundSpec = new TestOutboundSpec();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICustomInboundSpec>(inboundSpec);
        services.AddSingleton<ICustomOutboundSpec>(outboundSpec);
        services.AddPolymerDispatcher(configuration.GetSection("polymer"));

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<PolymerDispatcher>();

        var components = dispatcher.Introspect().Components;
        Assert.Contains(components, component => component.Name == "ws-inbound");
        Assert.Equal("/ws", inboundSpec.LastEndpoint);

        var clientConfig = dispatcher.ClientConfig("search");
        Assert.True(clientConfig.TryGetUnary("primary", out var outbound));
        var testOutbound = Assert.IsType<TestUnaryOutbound>(outbound);
        Assert.Equal("http://search.internal:8080", testOutbound.Address);
    }

    [Fact]
    public void AddPolymerDispatcher_UsesCustomPeerSpec()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["polymer:service"] = "analytics",
                ["polymer:outbounds:reports:unary:grpc:0:addresses:0"] = "http://127.0.0.1:9090",
                ["polymer:outbounds:reports:unary:grpc:0:remoteService"] = "reports",
                ["polymer:outbounds:reports:unary:grpc:0:peer:spec"] = TestPeerChooserSpec.SpecName,
                ["polymer:outbounds:reports:unary:grpc:0:peer:mode"] = "sticky",
                ["polymer:outbounds:reports:unary:grpc:0:peer:settings:mode"] = "sticky"
            }!)
            .Build();

        var peerSpec = new TestPeerChooserSpec();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICustomPeerChooserSpec>(peerSpec);
        services.AddPolymerDispatcher(configuration.GetSection("polymer"));

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<PolymerDispatcher>();

        var clientConfig = dispatcher.ClientConfig("reports");
        Assert.True(clientConfig.TryGetUnary(OutboundCollection.DefaultKey, out var outbound));
        Assert.IsType<Polymer.Transport.Grpc.GrpcOutbound>(outbound);
        Assert.Equal("sticky", peerSpec.LastMode);
    }

    private sealed class TestInboundSpec : ICustomInboundSpec
    {
        public const string SpecName = "test-inbound";

        public string? LastEndpoint { get; private set; }

        public string Name => SpecName;

        public ILifecycle CreateInbound(IConfigurationSection configuration, IServiceProvider services)
        {
            LastEndpoint = configuration["endpoint"];
            return new TestInbound();
        }
    }

    private sealed class TestInbound : ILifecycle
    {
        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class TestOutboundSpec : ICustomOutboundSpec
    {
        public const string SpecName = "test-outbound";

        public string? LastAddress { get; private set; }

        public string Name => SpecName;

        public IUnaryOutbound? CreateUnaryOutbound(IConfigurationSection configuration, IServiceProvider services)
        {
            LastAddress = configuration["url"];
            return new TestUnaryOutbound(LastAddress ?? string.Empty);
        }
    }

    private sealed class TestUnaryOutbound(string address) : IUnaryOutbound
    {
        public string Address { get; } = address;

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(Polymer.Errors.PolymerErrorAdapter.FromStatus(Polymer.Errors.PolymerStatusCode.Unimplemented, "test")));
    }

    private sealed class TestPeerChooserSpec : ICustomPeerChooserSpec
    {
        public const string SpecName = "test-peer";

        public string? LastMode { get; private set; }

        public string Name => SpecName;

        public Func<IReadOnlyList<IPeer>, IPeerChooser> CreateFactory(IConfigurationSection configuration, IServiceProvider services)
        {
            LastMode = configuration["mode"] ?? configuration.GetSection("settings")["mode"];
            return peers => new TestPeerChooser(peers);
        }
    }

    private sealed class TestPeerChooser(IReadOnlyList<IPeer> peers) : IPeerChooser
    {
        private readonly IReadOnlyList<IPeer> _peers = peers;

        public ValueTask<Result<PeerLease>> AcquireAsync(RequestMeta meta, CancellationToken cancellationToken = default)
        {
            if (_peers.Count == 0)
            {
                return ValueTask.FromResult(Err<PeerLease>(Polymer.Errors.PolymerErrorAdapter.FromStatus(Polymer.Errors.PolymerStatusCode.Unavailable, "no peers")));
            }

            var peer = _peers[0];
            peer.TryAcquire(cancellationToken);
            return ValueTask.FromResult(Ok(new PeerLease(peer, meta)));
        }
    }
}
