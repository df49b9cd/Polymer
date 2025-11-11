using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Serialization;
using Hugo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmniRelay.Configuration;
using OmniRelay.Core;
using OmniRelay.Core.Peers;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Transport.Http;
using Xunit;
using static Hugo.Go;
using OmniRelayDispatcher = OmniRelay.Dispatcher.Dispatcher;

namespace OmniRelay.Tests.Configuration;

public class OmniRelayConfigurationTests
{
    [Fact]
    public void AddOmniRelayDispatcher_BuildsDispatcherFromConfiguration()
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
                ["polymer:logging:overrides:OmniRelay.Transport.Http"] = "Trace"
            }!)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOmniRelayDispatcher(configuration.GetSection("polymer"));

        using var provider = services.BuildServiceProvider();

        var dispatcher = provider.GetRequiredService<OmniRelayDispatcher>();
        Assert.Equal("gateway", dispatcher.ServiceName);

        var clientConfig = dispatcher.ClientConfigOrThrow("keyvalue");
        Assert.True(clientConfig.TryGetUnary("primary", out var unary));
        Assert.NotNull(unary);
        Assert.True(clientConfig.TryGetOneway("primary", out var oneway));
        Assert.NotNull(oneway);

        var loggerOptions = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value;
        Assert.Equal(LogLevel.Warning, loggerOptions.MinLevel);
        Assert.Contains(
            loggerOptions.Rules,
            rule => string.Equals(rule.CategoryName, "OmniRelay.Transport.Http", StringComparison.Ordinal) &&
                    rule.LogLevel == LogLevel.Trace);

        var hostedServices = provider.GetServices<IHostedService>().ToList();
        Assert.Contains(hostedServices, service => service is DispatcherHostedService);
    }

    [Fact]
    public void AddOmniRelayDispatcher_MissingServiceThrows()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        var services = new ServiceCollection();

        Assert.Throws<OmniRelayConfigurationException>(
            () => services.AddOmniRelayDispatcher(configuration.GetSection("polymer")));
    }

    [Fact]
    public void AddOmniRelayDispatcher_InvalidPeerChooserThrows()
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
        services.AddOmniRelayDispatcher(configuration.GetSection("polymer"));

        using var provider = services.BuildServiceProvider();
        Assert.Throws<OmniRelayConfigurationException>(() => provider.GetRequiredService<OmniRelayDispatcher>());
    }

    [Fact]
    public void AddOmniRelayDispatcher_HttpsInboundWithoutTls_Throws()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["polymer:service"] = "gateway",
                ["polymer:inbounds:http:0:urls:0"] = "https://127.0.0.1:8443"
            }!)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOmniRelayDispatcher(configuration.GetSection("polymer"));

        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<OmniRelayConfigurationException>(() => provider.GetRequiredService<OmniRelayDispatcher>());
        Assert.Contains("no TLS certificate was configured", ex.Message);
    }

    [Fact]
    public void AddOmniRelayDispatcher_InvalidGrpcTlsCertificatePath_Throws()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["polymer:service"] = "gateway",
                ["polymer:inbounds:grpc:0:urls:0"] = "https://127.0.0.1:9090",
                ["polymer:inbounds:grpc:0:tls:certificatePath"] = "/tmp/omnirelay-grpc-missing.pfx"
            }!)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOmniRelayDispatcher(configuration.GetSection("polymer"));

        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<OmniRelayConfigurationException>(() => provider.GetRequiredService<OmniRelayDispatcher>());
        Assert.Contains("gRPC server certificate", ex.Message);
    }

    [Fact]
    public void AddOmniRelayDispatcher_UsesCustomTransportSpecs()
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
        services.AddOmniRelayDispatcher(configuration.GetSection("polymer"));

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<OmniRelayDispatcher>();

        var components = dispatcher.Introspect().Components;
        Assert.Contains(components, component => component.Name == "ws-inbound");
        Assert.Equal("/ws", TestInboundSpec.LastEndpoint);

        var clientConfig = dispatcher.ClientConfigOrThrow("search");
        Assert.True(clientConfig.TryGetUnary("primary", out var outbound));
        var testOutbound = Assert.IsType<TestUnaryOutbound>(outbound);
        Assert.Equal("http://search.internal:8080", testOutbound.Address);
    }

    [Fact]
    public void AddOmniRelayDispatcher_UsesNamedHttpClientFactoryClients()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["polymer:service"] = "metrics",
                ["polymer:inbounds:http:0:urls:0"] = "http://127.0.0.1:8080",
                ["polymer:outbounds:metrics:unary:http:0:key"] = "primary",
                ["polymer:outbounds:metrics:unary:http:0:url"] = "http://127.0.0.1:9095",
                ["polymer:outbounds:metrics:unary:http:0:clientName"] = "metrics"
            }!)
            .Build();

        var factory = new RecordingHttpClientFactory();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpClientFactory>(factory);
        services.AddOmniRelayDispatcher(configuration.GetSection("polymer"));

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<OmniRelayDispatcher>();

        var clientConfig = dispatcher.ClientConfigOrThrow("metrics");
        Assert.True(clientConfig.TryGetUnary("primary", out var outbound));
        Assert.IsType<HttpOutbound>(outbound);

        Assert.Equal(new[] { "metrics" }, factory.CreatedNames);
    }

    [Fact]
    public void AddOmniRelayDispatcher_UsesCustomPeerSpec()
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
        services.AddOmniRelayDispatcher(configuration.GetSection("polymer"));

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<OmniRelayDispatcher>();

        var clientConfig = dispatcher.ClientConfigOrThrow("reports");
        Assert.True(clientConfig.TryGetUnary(OutboundRegistry.DefaultKey, out var outbound));
        Assert.IsType<OmniRelay.Transport.Grpc.GrpcOutbound>(outbound);
        Assert.Equal("sticky", TestPeerChooserSpec.LastMode);
    }

    [Fact]
    public void AddOmniRelayDispatcher_ConfiguresJsonCodecs()
    {
        var schemaPath = Path.Combine(Path.GetTempPath(), $"polymer-schema-{Guid.NewGuid():N}.json");
        File.WriteAllText(schemaPath, "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"count\":{\"type\":\"integer\"}},\"required\":[\"name\",\"count\"]}");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["polymer:service"] = "echo",
                    ["polymer:encodings:json:inbound:0:procedure"] = "echo::call",
                    ["polymer:encodings:json:inbound:0:kind"] = "Unary",
                    ["polymer:encodings:json:inbound:0:requestType"] = typeof(EchoRequest).AssemblyQualifiedName,
                    ["polymer:encodings:json:inbound:0:responseType"] = typeof(EchoResponse).AssemblyQualifiedName,
                    ["polymer:encodings:json:inbound:0:encoding"] = "application/json",
                    ["polymer:encodings:json:inbound:0:options:propertyNameCaseInsensitive"] = "false",
                    ["polymer:encodings:json:inbound:0:schemas:request"] = schemaPath,
                    ["polymer:encodings:json:outbound:0:service"] = "remote",
                    ["polymer:encodings:json:outbound:0:procedure"] = "echo::call",
                    ["polymer:encodings:json:outbound:0:kind"] = "Unary",
                    ["polymer:encodings:json:outbound:0:requestType"] = typeof(EchoRequest).AssemblyQualifiedName,
                    ["polymer:encodings:json:outbound:0:responseType"] = typeof(EchoResponse).AssemblyQualifiedName,
                    ["polymer:encodings:json:outbound:0:profile"] = "strict",
                    ["polymer:encodings:json:outbound:0:context"] = typeof(EchoJsonContext).AssemblyQualifiedName,
                    ["polymer:encodings:json:profiles:strict:options:propertyNameCaseInsensitive"] = "false"
                }!)
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOmniRelayDispatcher(configuration.GetSection("polymer"));

            using var provider = services.BuildServiceProvider();
            var dispatcher = provider.GetRequiredService<OmniRelayDispatcher>();

            Assert.True(dispatcher.Codecs.TryResolve<EchoRequest, EchoResponse>(
                ProcedureCodecScope.Inbound,
                "echo",
                "echo::call",
                ProcedureKind.Unary,
                out var inboundCodec));
            Assert.Equal("application/json", inboundCodec.Encoding);

            var invalidPayload = new ReadOnlyMemory<byte>("{\"count\":2}"u8.ToArray());
            var decode = inboundCodec.DecodeRequest(invalidPayload, new RequestMeta(service: "echo", procedure: "echo::call"));
            Assert.True(decode.IsFailure);
            Assert.Equal(OmniRelayStatusCode.InvalidArgument, OmniRelayErrorAdapter.ToStatus(decode.Error!));

            Assert.True(dispatcher.Codecs.TryResolve<EchoRequest, EchoResponse>(
                ProcedureCodecScope.Outbound,
                "remote",
                "echo::call",
                ProcedureKind.Unary,
                out var outboundCodec));
            Assert.Equal("application/json", outboundCodec.Encoding);
        }
        finally
        {
            if (File.Exists(schemaPath))
            {
                File.Delete(schemaPath);
            }
        }
    }

    private sealed class TestInboundSpec : ICustomInboundSpec
    {
        public const string SpecName = "test-inbound";

        public static string? LastEndpoint { get; private set; }

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

        public static string? LastAddress { get; private set; }

        public string Name => SpecName;

        public IUnaryOutbound CreateUnaryOutbound(IConfigurationSection configuration, IServiceProvider services)
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
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unimplemented, "test")));
    }

    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        public List<string> CreatedNames { get; } = [];

        public HttpClient CreateClient(string name = "")
        {
            CreatedNames.Add(name);
            return new HttpClient(new StubHandler());
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class TestPeerChooserSpec : ICustomPeerChooserSpec
    {
        public const string SpecName = "test-peer";

        public static string? LastMode { get; private set; }

        public string Name => SpecName;

        public Func<IReadOnlyList<IPeer>, IPeerChooser> CreateFactory(IConfigurationSection configuration, IServiceProvider services)
        {
            LastMode = configuration["mode"] ?? configuration.GetSection("settings")["mode"];
            return peers => new TestPeerChooser(peers);
        }
    }

    private sealed class TestPeerChooser(IEnumerable<IPeer> peers) : IPeerChooser
    {
        private IReadOnlyList<IPeer> _peers = peers?.ToList() ?? [];

        public void UpdatePeers(IEnumerable<IPeer> peers)
        {
            ArgumentNullException.ThrowIfNull(peers);
            _peers = peers.ToList();
        }

        public ValueTask<Result<PeerLease>> AcquireAsync(RequestMeta meta, CancellationToken cancellationToken = default)
        {
            if (_peers.Count == 0)
            {
                return ValueTask.FromResult(Err<PeerLease>(OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "no peers")));
            }

            var peer = _peers[0];
            peer.TryAcquire(cancellationToken);
            return ValueTask.FromResult(Ok(new PeerLease(peer, meta)));
        }

        public void Dispose()
        {
        }
    }
}

[JsonSerializable(typeof(EchoRequest))]
[JsonSerializable(typeof(EchoResponse))]
internal partial class EchoJsonContext : JsonSerializerContext
{
}

internal sealed record EchoRequest(string Name, int Count)
{
    public string Name { get; init; } = Name;

    public int Count { get; init; } = Count;
}

internal sealed record EchoResponse(string Message)
{
    public string Message { get; init; } = Message;
}
