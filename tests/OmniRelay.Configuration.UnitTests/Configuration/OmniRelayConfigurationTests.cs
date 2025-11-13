using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using OmniRelay.Core;
using OmniRelay.Core.Peers;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Transport.Http;
using Shouldly;
using Xunit;
using OmniRelayDispatcher = OmniRelay.Dispatcher.Dispatcher;

namespace OmniRelay.Configuration.UnitTests.Configuration;

public class OmniRelayConfigurationTests
{
    private const string CustomInboundSpecName = "test-inbound";
    private const string CustomOutboundSpecName = "test-outbound";
    private const string CustomPeerSpecName = "test-peer";

    [Fact]
    public void AddOmniRelayDispatcher_BuildsDispatcherFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["omnirelay:service"] = "gateway",
                ["omnirelay:inbounds:http:0:urls:0"] = "http://127.0.0.1:8080",
                ["omnirelay:outbounds:keyvalue:unary:http:0:key"] = "primary",
                ["omnirelay:outbounds:keyvalue:unary:http:0:url"] = "http://127.0.0.1:8081",
                ["omnirelay:outbounds:keyvalue:oneway:http:0:key"] = "primary",
                ["omnirelay:outbounds:keyvalue:oneway:http:0:url"] = "http://127.0.0.1:8081",
                ["omnirelay:logging:level"] = "Warning",
                ["omnirelay:logging:overrides:OmniRelay.Transport.Http"] = "Trace"
            }!)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOmniRelayDispatcher(configuration.GetSection("omnirelay"));

        using var provider = services.BuildServiceProvider();

        var dispatcher = provider.GetRequiredService<OmniRelayDispatcher>();
        dispatcher.ServiceName.ShouldBe("gateway");

        var clientConfig = dispatcher.ClientConfigOrThrow("keyvalue");
        clientConfig.TryGetUnary("primary", out var unary).ShouldBeTrue();
        unary.ShouldNotBeNull();
        clientConfig.TryGetOneway("primary", out var oneway).ShouldBeTrue();
        oneway.ShouldNotBeNull();

        var loggerOptions = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value;
        loggerOptions.MinLevel.ShouldBe(LogLevel.Warning);
        loggerOptions.Rules.ShouldContain(
            rule => string.Equals(rule.CategoryName, "OmniRelay.Transport.Http", StringComparison.Ordinal) &&
                    rule.LogLevel == LogLevel.Trace);

        var hostedServices = provider.GetServices<IHostedService>().ToList();
        hostedServices.ShouldContain(service => service is DispatcherHostedService);
    }

    [Fact]
    public void AddOmniRelayDispatcher_MissingServiceThrows()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        var services = new ServiceCollection();

        Should.Throw<OmniRelayConfigurationException>(
            () => services.AddOmniRelayDispatcher(configuration.GetSection("omnirelay")));
    }

    [Fact]
    public void AddOmniRelayDispatcher_InvalidPeerChooserThrows()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["omnirelay:service"] = "edge",
                ["omnirelay:outbounds:inventory:stream:grpc:0:addresses:0"] = "http://127.0.0.1:9090",
                ["omnirelay:outbounds:inventory:stream:grpc:0:peerChooser"] = "random-weighted"
            }!)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOmniRelayDispatcher(configuration.GetSection("omnirelay"));

        using var provider = services.BuildServiceProvider();
        Should.Throw<OmniRelayConfigurationException>(() => provider.GetRequiredService<OmniRelayDispatcher>());
    }

    [Fact]
    public void AddOmniRelayDispatcher_HttpsInboundWithoutTls_Throws()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["omnirelay:service"] = "gateway",
                ["omnirelay:inbounds:http:0:urls:0"] = "https://127.0.0.1:8443"
            }!)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOmniRelayDispatcher(configuration.GetSection("omnirelay"));

        using var provider = services.BuildServiceProvider();
        var ex = Should.Throw<OmniRelayConfigurationException>(() => provider.GetRequiredService<OmniRelayDispatcher>());
        ex.Message.ShouldContain("no TLS certificate was configured");
    }

    [Fact]
    public void AddOmniRelayDispatcher_InvalidGrpcTlsCertificatePath_Throws()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["omnirelay:service"] = "gateway",
                ["omnirelay:inbounds:grpc:0:urls:0"] = "https://127.0.0.1:9090",
                ["omnirelay:inbounds:grpc:0:tls:certificatePath"] = "/tmp/omnirelay-grpc-missing.pfx"
            }!)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOmniRelayDispatcher(configuration.GetSection("omnirelay"));

        using var provider = services.BuildServiceProvider();
        var ex = Should.Throw<OmniRelayConfigurationException>(() => provider.GetRequiredService<OmniRelayDispatcher>());
        ex.Message.ShouldContain("gRPC server TLS certificate");
    }

    [Fact]
    public void AddOmniRelayDispatcher_InvalidGrpcTlsCertificateData_Throws()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["omnirelay:service"] = "gateway",
                ["omnirelay:inbounds:grpc:0:urls:0"] = "https://127.0.0.1:9090",
                ["omnirelay:inbounds:grpc:0:tls:certificateData"] = "not-base64!!"
            }!)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOmniRelayDispatcher(configuration.GetSection("omnirelay"));

        using var provider = services.BuildServiceProvider();
        var ex = Should.Throw<OmniRelayConfigurationException>(() => provider.GetRequiredService<OmniRelayDispatcher>());
        ex.Message.ShouldContain("certificateData");
    }

    [Fact]
    public void AddOmniRelayDispatcher_UsesCustomTransportSpecs()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["omnirelay:service"] = "custom",
                ["omnirelay:inbounds:custom:0:spec"] = CustomInboundSpecName,
                ["omnirelay:inbounds:custom:0:name"] = "ws-inbound",
                ["omnirelay:inbounds:custom:0:endpoint"] = "/ws",
                ["omnirelay:outbounds:search:unary:custom:0:spec"] = CustomOutboundSpecName,
                ["omnirelay:outbounds:search:unary:custom:0:key"] = "primary",
                ["omnirelay:outbounds:search:unary:custom:0:url"] = "http://search.internal:8080"
            }!)
            .Build();

        string? capturedInboundEndpoint = null;
        string? capturedOutboundAddress = null;

        var inboundSpec = Substitute.For<ICustomInboundSpec>();
        inboundSpec.Name.Returns(CustomInboundSpecName);
        inboundSpec.CreateInbound(Arg.Any<IConfigurationSection>(), Arg.Any<IServiceProvider>())
            .Returns(callInfo =>
            {
                capturedInboundEndpoint = callInfo.Arg<IConfigurationSection>()["endpoint"];
                return CreateLifecycleSubstitute<ILifecycle>();
            });

        var outboundSpec = Substitute.For<ICustomOutboundSpec>();
        outboundSpec.Name.Returns(CustomOutboundSpecName);
        var customOutbound = CreateLifecycleSubstitute<IUnaryOutbound>();
        outboundSpec.CreateUnaryOutbound(Arg.Any<IConfigurationSection>(), Arg.Any<IServiceProvider>())
            .Returns(callInfo =>
            {
                capturedOutboundAddress = callInfo.Arg<IConfigurationSection>()["url"];
                return customOutbound;
            });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICustomInboundSpec>(inboundSpec);
        services.AddSingleton<ICustomOutboundSpec>(outboundSpec);
        services.AddOmniRelayDispatcher(configuration.GetSection("omnirelay"));

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<OmniRelayDispatcher>();

        var components = dispatcher.Introspect().Components;
        components.ShouldContain(component => component.Name == "ws-inbound");
        capturedInboundEndpoint.ShouldBe("/ws");

        var clientConfig = dispatcher.ClientConfigOrThrow("search");
        clientConfig.TryGetUnary("primary", out var outbound).ShouldBeTrue();
        outbound.ShouldBeSameAs(customOutbound);
        capturedOutboundAddress.ShouldBe("http://search.internal:8080");
    }

    [Fact]
    public void AddOmniRelayDispatcher_UsesNamedHttpClientFactoryClients()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["omnirelay:service"] = "metrics",
                ["omnirelay:inbounds:http:0:urls:0"] = "http://127.0.0.1:8080",
                ["omnirelay:outbounds:metrics:unary:http:0:key"] = "primary",
                ["omnirelay:outbounds:metrics:unary:http:0:url"] = "http://127.0.0.1:9095",
                ["omnirelay:outbounds:metrics:unary:http:0:clientName"] = "metrics"
            }!)
            .Build();

        var createdNames = new List<string>();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(callInfo =>
            {
                createdNames.Add(callInfo.Arg<string>());
                return new HttpClient(new HttpClientHandler());
            });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(httpClientFactory);
        services.AddOmniRelayDispatcher(configuration.GetSection("omnirelay"));

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<OmniRelayDispatcher>();

        var clientConfig = dispatcher.ClientConfigOrThrow("metrics");
        clientConfig.TryGetUnary("primary", out var outbound).ShouldBeTrue();
        outbound.ShouldBeOfType<HttpOutbound>();

        createdNames.ShouldBe(new[] { "metrics" });
    }

    [Fact]
    public void AddOmniRelayDispatcher_UsesCustomPeerSpec()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["omnirelay:service"] = "analytics",
                ["omnirelay:outbounds:reports:unary:grpc:0:addresses:0"] = "http://127.0.0.1:9090",
                ["omnirelay:outbounds:reports:unary:grpc:0:remoteService"] = "reports",
                ["omnirelay:outbounds:reports:unary:grpc:0:peer:spec"] = CustomPeerSpecName,
                ["omnirelay:outbounds:reports:unary:grpc:0:peer:mode"] = "sticky",
                ["omnirelay:outbounds:reports:unary:grpc:0:peer:settings:mode"] = "sticky"
            }!)
            .Build();

        string? capturedMode = null;
        var peerChooser = Substitute.For<IPeerChooser>();
        var peerSpec = Substitute.For<ICustomPeerChooserSpec>();
        peerSpec.Name.Returns(CustomPeerSpecName);
        peerSpec.CreateFactory(Arg.Any<IConfigurationSection>(), Arg.Any<IServiceProvider>())
            .Returns(callInfo =>
            {
                var section = callInfo.Arg<IConfigurationSection>();
                capturedMode = section["mode"] ?? section.GetSection("settings")["mode"];
                return _ => peerChooser;
            });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICustomPeerChooserSpec>(peerSpec);
        services.AddOmniRelayDispatcher(configuration.GetSection("omnirelay"));

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<OmniRelayDispatcher>();

        var clientConfig = dispatcher.ClientConfigOrThrow("reports");
        clientConfig.TryGetUnary(OutboundRegistry.DefaultKey, out var outbound).ShouldBeTrue();
        outbound.ShouldBeOfType<OmniRelay.Transport.Grpc.GrpcOutbound>();
        capturedMode.ShouldBe("sticky");
    }

    [Fact]
    public void AddOmniRelayDispatcher_ConfiguresJsonCodecs()
    {
        var schemaPath = Path.Combine(Path.GetTempPath(), $"omnirelay-schema-{Guid.NewGuid():N}.json");
        File.WriteAllText(schemaPath, "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"count\":{\"type\":\"integer\"}},\"required\":[\"name\",\"count\"]}");

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["omnirelay:service"] = "echo",
                    ["omnirelay:encodings:json:inbound:0:procedure"] = "echo::call",
                    ["omnirelay:encodings:json:inbound:0:kind"] = "Unary",
                    ["omnirelay:encodings:json:inbound:0:requestType"] = typeof(EchoRequest).AssemblyQualifiedName,
                    ["omnirelay:encodings:json:inbound:0:responseType"] = typeof(EchoResponse).AssemblyQualifiedName,
                    ["omnirelay:encodings:json:inbound:0:encoding"] = "application/json",
                    ["omnirelay:encodings:json:inbound:0:options:propertyNameCaseInsensitive"] = "false",
                    ["omnirelay:encodings:json:inbound:0:schemas:request"] = schemaPath,
                    ["omnirelay:encodings:json:outbound:0:service"] = "remote",
                    ["omnirelay:encodings:json:outbound:0:procedure"] = "echo::call",
                    ["omnirelay:encodings:json:outbound:0:kind"] = "Unary",
                    ["omnirelay:encodings:json:outbound:0:requestType"] = typeof(EchoRequest).AssemblyQualifiedName,
                    ["omnirelay:encodings:json:outbound:0:responseType"] = typeof(EchoResponse).AssemblyQualifiedName,
                    ["omnirelay:encodings:json:outbound:0:profile"] = "strict",
                    ["omnirelay:encodings:json:outbound:0:context"] = typeof(EchoJsonContext).AssemblyQualifiedName,
                    ["omnirelay:encodings:json:profiles:strict:options:propertyNameCaseInsensitive"] = "false"
                }!)
                .Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOmniRelayDispatcher(configuration.GetSection("omnirelay"));

            using var provider = services.BuildServiceProvider();
            var dispatcher = provider.GetRequiredService<OmniRelayDispatcher>();

            dispatcher.Codecs.TryResolve<EchoRequest, EchoResponse>(
                ProcedureCodecScope.Inbound,
                "echo",
                "echo::call",
                ProcedureKind.Unary,
                out var inboundCodec).ShouldBeTrue();
            inboundCodec.Encoding.ShouldBe("application/json");

            var invalidPayload = new ReadOnlyMemory<byte>("{\"count\":2}"u8.ToArray());
            var decode = inboundCodec.DecodeRequest(invalidPayload, new RequestMeta(service: "echo", procedure: "echo::call"));
            decode.IsFailure.ShouldBeTrue();
            OmniRelayErrorAdapter.ToStatus(decode.Error!).ShouldBe(OmniRelayStatusCode.InvalidArgument);

            dispatcher.Codecs.TryResolve<EchoRequest, EchoResponse>(
                ProcedureCodecScope.Outbound,
                "remote",
                "echo::call",
                ProcedureKind.Unary,
                out var outboundCodec).ShouldBeTrue();
            outboundCodec.Encoding.ShouldBe("application/json");
        }
        finally
        {
            if (File.Exists(schemaPath))
            {
                File.Delete(schemaPath);
            }
        }
    }

    private static T CreateLifecycleSubstitute<T>() where T : class, ILifecycle
    {
        var lifecycle = Substitute.For<T>();
        lifecycle.StartAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        lifecycle.StopAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        return lifecycle;
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
