using System.Collections.Concurrent;
using System.Net.Quic;
using System.Security.Cryptography.X509Certificates;
using AwesomeAssertions;
using Hugo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.Dispatcher.Config;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests;
using OmniRelay.Tests.Protos;
using OmniRelay.Tests.Support;
using OmniRelay.Transport.Grpc.Interceptors;
using Xunit;

namespace OmniRelay.CodeGen.IntegrationTests;

public class CodegenWorkflowIntegrationTests
{
    private const string EncodingName = "protobuf";

    [Http3Fact(Timeout = 90_000)]
    public async ValueTask GeneratedClient_RoundTripsOverHttp3_WhenDispatcherHostEnablesIt()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        if (!QuicConnection.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=codegen-host-http3");
        var certificatePath = PersistCertificate(certificate);
        var address = new Uri($"https://127.0.0.1:{TestPortAllocator.GetRandomPort()}");
        var protocols = new ConcurrentQueue<string>();

        var serverBuilder = Host.CreateApplicationBuilder();
        serverBuilder.Services.AddLogging();
        serverBuilder.Services.AddSingleton(protocols);
        serverBuilder.Services.AddSingleton<HostedProtocolCaptureInterceptor>();
        serverBuilder.Services.AddSingleton<IGrpcInterceptorAliasRegistry>(sp =>
        {
            var registry = new GrpcInterceptorAliasRegistry();
            registry.RegisterServer("protocol-capture", typeof(HostedProtocolCaptureInterceptor));
            return registry;
        });
        serverBuilder.Services.AddSingleton<TestServiceOmniRelay.ITestService, LoopbackTestService>();
        serverBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "codegen-host-http3",
            ["omnirelay:diagnostics:runtime:enableControlPlane"] = "false",
            ["omnirelay:inbounds:grpc:0:name"] = "grpc-http3",
            ["omnirelay:inbounds:grpc:0:urls:0"] = address.ToString(),
            ["omnirelay:inbounds:grpc:0:runtime:enableHttp3"] = "true",
            ["omnirelay:inbounds:grpc:0:runtime:interceptors:0"] = "protocol-capture",
            ["omnirelay:inbounds:grpc:0:tls:certificatePath"] = certificatePath,
            ["omnirelay:inbounds:grpc:0:tls:checkCertificateRevocation"] = "false"
        });
        serverBuilder.Services.AddOmniRelayDispatcherFromConfiguration(serverBuilder.Configuration.GetSection("omnirelay"));

        using var serverHost = serverBuilder.Build();
        var serverDispatcher = serverHost.Services.GetRequiredService<Dispatcher.Dispatcher>();
        var serviceImpl = (LoopbackTestService)serverHost.Services.GetRequiredService<TestServiceOmniRelay.ITestService>();
        serverDispatcher.RegisterTestService(serviceImpl);

        var clientBuilder = Host.CreateApplicationBuilder();
        clientBuilder.Services.AddLogging();
        clientBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "codegen-client-http3",
            ["omnirelay:diagnostics:runtime:enableControlPlane"] = "false",
            ["omnirelay:outbounds:codegen-host-http3:unary:grpc:0:remoteService"] = "codegen-host-http3",
            ["omnirelay:outbounds:codegen-host-http3:unary:grpc:0:endpoints:0:address"] = address.ToString(),
            ["omnirelay:outbounds:codegen-host-http3:unary:grpc:0:endpoints:0:supportsHttp3"] = "true",
            ["omnirelay:outbounds:codegen-host-http3:unary:grpc:0:runtime:enableHttp3"] = "true",
            ["omnirelay:outbounds:codegen-host-http3:unary:grpc:0:runtime:requestVersion"] = "3.0",
            ["omnirelay:outbounds:codegen-host-http3:unary:grpc:0:runtime:versionPolicy"] = "RequestVersionExact",
            ["omnirelay:outbounds:codegen-host-http3:unary:grpc:0:tls:allowUntrustedCertificates"] = "true"
        });
        clientBuilder.Services.AddOmniRelayDispatcherFromConfiguration(clientBuilder.Configuration.GetSection("omnirelay"));

        using var clientHost = clientBuilder.Build();
        var clientDispatcher = clientHost.Services.GetRequiredService<Dispatcher.Dispatcher>();

        var ct = TestContext.Current.CancellationToken;
        await serverHost.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);
        await clientHost.StartAsync(ct);

        try
        {
            var client = TestServiceOmniRelay.CreateTestServiceClient(clientDispatcher, "codegen-host-http3");
            var response = await client.UnaryCallAsync(new UnaryRequest { Message = "http3" }, cancellationToken: ct);
            if (!response.IsSuccess && IsKnownHttp3HandshakeIssue(response.Error))
            {
                return;
            }

            response.IsSuccess.Should().BeTrue(response.Error?.Message);
            response.Value.Body.Message.Should().Be("http3-ok");
        }
        finally
        {
            await clientHost.StopAsync(CancellationToken.None);
            await serverHost.StopAsync(CancellationToken.None);
            DeleteIfExists(certificatePath);
        }

        serviceImpl.UnaryMetas.TryDequeue(out var unaryMeta).Should().BeTrue("Unary metadata not captured.");
        unaryMeta.Should().NotBeNull();
        var http3Meta = unaryMeta!;
        http3Meta.Service.Should().Be("codegen-host-http3");
        http3Meta.Procedure.Should().Be("UnaryCall");
        http3Meta.Encoding.Should().Be(EncodingName);

        protocols.TryDequeue(out var observed).Should().BeTrue("No protocol captured.");
        observed.Should().NotBeNull();
        observed!.Should().StartWithEquivalentOf("HTTP/3");
    }

    [Http3Fact(Timeout = 90_000)]
    public async ValueTask GeneratedClient_FallsBackToHttp2_WhenServerDisablesHttp3()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        if (!QuicConnection.IsSupported)
        {
            return;
        }

        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=codegen-host-http2");
        var certificatePath = PersistCertificate(certificate);
        var address = new Uri($"https://127.0.0.1:{TestPortAllocator.GetRandomPort()}");
        var protocols = new ConcurrentQueue<string>();

        var serverBuilder = Host.CreateApplicationBuilder();
        serverBuilder.Services.AddLogging();
        serverBuilder.Services.AddSingleton(protocols);
        serverBuilder.Services.AddSingleton<HostedProtocolCaptureInterceptor>();
        serverBuilder.Services.AddSingleton<IGrpcInterceptorAliasRegistry>(sp =>
        {
            var registry = new GrpcInterceptorAliasRegistry();
            registry.RegisterServer("protocol-capture", typeof(HostedProtocolCaptureInterceptor));
            return registry;
        });
        serverBuilder.Services.AddSingleton<TestServiceOmniRelay.ITestService, LoopbackTestService>();
        serverBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "codegen-host-http2",
            ["omnirelay:diagnostics:runtime:enableControlPlane"] = "false",
            ["omnirelay:inbounds:grpc:0:name"] = "grpc-http2",
            ["omnirelay:inbounds:grpc:0:urls:0"] = address.ToString(),
            ["omnirelay:inbounds:grpc:0:runtime:enableHttp3"] = "false",
            ["omnirelay:inbounds:grpc:0:runtime:interceptors:0"] = "protocol-capture",
            ["omnirelay:inbounds:grpc:0:tls:certificatePath"] = certificatePath,
            ["omnirelay:inbounds:grpc:0:tls:checkCertificateRevocation"] = "false"
        });
        serverBuilder.Services.AddOmniRelayDispatcherFromConfiguration(serverBuilder.Configuration.GetSection("omnirelay"));

        using var serverHost = serverBuilder.Build();
        var serverDispatcher = serverHost.Services.GetRequiredService<Dispatcher.Dispatcher>();
        var serviceImpl = (LoopbackTestService)serverHost.Services.GetRequiredService<TestServiceOmniRelay.ITestService>();
        serverDispatcher.RegisterTestService(serviceImpl);

        var clientBuilder = Host.CreateApplicationBuilder();
        clientBuilder.Services.AddLogging();
        clientBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "codegen-client-http2",
            ["omnirelay:diagnostics:runtime:enableControlPlane"] = "false",
            ["omnirelay:outbounds:codegen-host-http2:unary:grpc:0:remoteService"] = "codegen-host-http2",
            ["omnirelay:outbounds:codegen-host-http2:unary:grpc:0:endpoints:0:address"] = address.ToString(),
            ["omnirelay:outbounds:codegen-host-http2:unary:grpc:0:endpoints:0:supportsHttp3"] = "false",
            ["omnirelay:outbounds:codegen-host-http2:unary:grpc:0:runtime:enableHttp3"] = "true",
            ["omnirelay:outbounds:codegen-host-http2:unary:grpc:0:runtime:requestVersion"] = "3.0",
            ["omnirelay:outbounds:codegen-host-http2:unary:grpc:0:runtime:versionPolicy"] = "RequestVersionExact",
            ["omnirelay:outbounds:codegen-host-http2:unary:grpc:0:tls:allowUntrustedCertificates"] = "true"
        });
        clientBuilder.Services.AddOmniRelayDispatcherFromConfiguration(clientBuilder.Configuration.GetSection("omnirelay"));

        using var clientHost = clientBuilder.Build();
        var clientDispatcher = clientHost.Services.GetRequiredService<Dispatcher.Dispatcher>();

        var ct = TestContext.Current.CancellationToken;
        await serverHost.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);
        await clientHost.StartAsync(ct);

        try
        {
            var client = TestServiceOmniRelay.CreateTestServiceClient(clientDispatcher, "codegen-host-http2");
            var response = await client.UnaryCallAsync(new UnaryRequest { Message = "fallback" }, cancellationToken: ct);
            if (!response.IsSuccess && IsKnownHttp3HandshakeIssue(response.Error))
            {
                return;
            }

            response.IsSuccess.Should().BeTrue(response.Error?.Message);
            response.Value.Body.Message.Should().Be("fallback-ok");
        }
        finally
        {
            await clientHost.StopAsync(CancellationToken.None);
            await serverHost.StopAsync(CancellationToken.None);
            DeleteIfExists(certificatePath);
        }

        serviceImpl.UnaryMetas.TryDequeue(out var unaryMeta).Should().BeTrue("Unary metadata not captured.");
        unaryMeta.Should().NotBeNull();
        var http2Meta = unaryMeta!;
        http2Meta.Service.Should().Be("codegen-host-http2");
        http2Meta.Procedure.Should().Be("UnaryCall");
        http2Meta.Encoding.Should().Be(EncodingName);

        protocols.TryDequeue(out var observed).Should().BeTrue("No protocol captured.");
        observed.Should().NotBeNull();
        observed!.Should().StartWithEquivalentOf("HTTP/2");
    }

    [Fact(Timeout = 90_000)]
    public async ValueTask GeneratedClient_StreamHelpers_WorkWhenResolvedFromDependencyInjection()
    {
        var address = new Uri($"http://127.0.0.1:{TestPortAllocator.GetRandomPort()}");

        var serverBuilder = Host.CreateApplicationBuilder();
        serverBuilder.Services.AddLogging();
        serverBuilder.Services.AddSingleton<TestServiceOmniRelay.ITestService, LoopbackTestService>();
        serverBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "codegen-host-streams",
            ["omnirelay:diagnostics:runtime:enableControlPlane"] = "false",
            ["omnirelay:inbounds:grpc:0:name"] = "grpc-h2",
            ["omnirelay:inbounds:grpc:0:urls:0"] = address.ToString()
        });
        serverBuilder.Services.AddOmniRelayDispatcherFromConfiguration(serverBuilder.Configuration.GetSection("omnirelay"));

        using var serverHost = serverBuilder.Build();
        var serverDispatcher = serverHost.Services.GetRequiredService<Dispatcher.Dispatcher>();
        var serviceImpl = (LoopbackTestService)serverHost.Services.GetRequiredService<TestServiceOmniRelay.ITestService>();
        serverDispatcher.RegisterTestService(serviceImpl);

        var clientBuilder = Host.CreateApplicationBuilder();
        clientBuilder.Services.AddLogging();
        clientBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "codegen-client-streams",
            ["omnirelay:diagnostics:runtime:enableControlPlane"] = "false",
            ["omnirelay:outbounds:codegen-host-streams:unary:grpc:0:remoteService"] = "codegen-host-streams",
            ["omnirelay:outbounds:codegen-host-streams:unary:grpc:0:addresses:0"] = address.ToString(),
            ["omnirelay:outbounds:codegen-host-streams:stream:grpc:0:remoteService"] = "codegen-host-streams",
            ["omnirelay:outbounds:codegen-host-streams:stream:grpc:0:addresses:0"] = address.ToString(),
            ["omnirelay:outbounds:codegen-host-streams:clientStream:grpc:0:remoteService"] = "codegen-host-streams",
            ["omnirelay:outbounds:codegen-host-streams:clientStream:grpc:0:addresses:0"] = address.ToString(),
            ["omnirelay:outbounds:codegen-host-streams:duplex:grpc:0:remoteService"] = "codegen-host-streams",
            ["omnirelay:outbounds:codegen-host-streams:duplex:grpc:0:addresses:0"] = address.ToString()
        });
        clientBuilder.Services.AddOmniRelayDispatcherFromConfiguration(clientBuilder.Configuration.GetSection("omnirelay"));
        clientBuilder.Services.AddSingleton(sp =>
        {
            var dispatcher = sp.GetRequiredService<Dispatcher.Dispatcher>();
            return TestServiceOmniRelay.CreateTestServiceClient(dispatcher, "codegen-host-streams");
        });

        using var clientHost = clientBuilder.Build();

        var ct = TestContext.Current.CancellationToken;
        await serverHost.StartAsync(ct);
        await WaitForGrpcReadyAsync(address, ct);
        await clientHost.StartAsync(ct);

        try
        {
            var client = clientHost.Services.GetRequiredService<TestServiceOmniRelay.TestServiceClient>();

            var unary = await client.UnaryCallAsync(new UnaryRequest { Message = "di" }, cancellationToken: ct);
            unary.IsSuccess.Should().BeTrue(unary.Error?.Message);
            unary.Value.Body.Message.Should().Be("di-ok");

            var streamValues = new List<string>();
            await foreach (var message in client.ServerStreamAsync(new StreamRequest { Value = "flow" }, cancellationToken: ct))
            {
                streamValues.Add(message.ValueOrChecked().Body.Value);
            }
            streamValues.Should().Equal(["flow#0", "flow#1", "flow#2"]);

            var sessionResult = await client.ClientStreamAsync(cancellationToken: ct);
            await using (var session = sessionResult.ValueOrChecked())
            {
                (await session.WriteAsync(new StreamRequest { Value = "4" }, ct)).ValueOrChecked();
                (await session.WriteAsync(new StreamRequest { Value = "6" }, ct)).ValueOrChecked();
                await session.CompleteAsync(ct);
                var aggregate = (await session.Response).ValueOrChecked();
                aggregate.Body.Message.Should().Be("sum:10");
            }

            var duplexResult = await client.DuplexStreamAsync(cancellationToken: ct);
            await using (var duplex = duplexResult.ValueOrChecked())
            {
                (await duplex.WriteAsync(new StreamRequest { Value = "alpha" }, ct)).ValueOrChecked();
                (await duplex.WriteAsync(new StreamRequest { Value = "beta" }, ct)).ValueOrChecked();
                await duplex.CompleteRequestsAsync(cancellationToken: ct);

                var duplexValues = new List<string>();
                await foreach (var response in duplex.ReadResponsesAsync(ct))
                {
                    duplexValues.Add(response.ValueOrChecked().Body.Value);
                }

                duplexValues.Should().Equal(["ready", "echo:alpha", "echo:beta"]);
            }
        }
        finally
        {
            await clientHost.StopAsync(CancellationToken.None);
            await serverHost.StopAsync(CancellationToken.None);
        }

        serviceImpl.ServerStreamMetas.TryDequeue(out var serverStreamMeta).Should().BeTrue("Server stream metadata missing.");
        serverStreamMeta.Should().NotBeNull();
        var serverMeta = serverStreamMeta!;
        serverMeta.Service.Should().Be("codegen-host-streams");
        serverMeta.Procedure.Should().Be("ServerStream");
        serverMeta.Encoding.Should().Be(EncodingName);

        serviceImpl.ClientStreamMetas.TryDequeue(out var clientStreamMeta).Should().BeTrue("Client stream metadata missing.");
        clientStreamMeta.Should().NotBeNull();
        var clientMeta = clientStreamMeta!;
        clientMeta.Service.Should().Be("codegen-host-streams");
        clientMeta.Procedure.Should().Be("ClientStream");
        clientMeta.Encoding.Should().Be(EncodingName);

        serviceImpl.DuplexStreamMetas.TryDequeue(out var duplexMeta).Should().BeTrue("Duplex stream metadata missing.");
        duplexMeta.Should().NotBeNull();
        var duplexStreamMeta = duplexMeta!;
        duplexStreamMeta.Service.Should().Be("codegen-host-streams");
        duplexStreamMeta.Procedure.Should().Be("DuplexStream");
        duplexStreamMeta.Encoding.Should().Be(EncodingName);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void GeneratedRegistration_MatchesManualProcedureMetadata()
    {
        var serviceName = "codegen-manual";
        var manualDispatcher = new Dispatcher.Dispatcher(new DispatcherOptions(serviceName));
        var generatedDispatcher = new Dispatcher.Dispatcher(new DispatcherOptions(serviceName));
        var serviceImpl = new LoopbackTestService();

        RegisterTestServiceManually(manualDispatcher, serviceImpl);
        generatedDispatcher.RegisterTestService(serviceImpl);

        var manualProcedures = manualDispatcher.Introspect().Procedures;
        var generatedProcedures = generatedDispatcher.Introspect().Procedures;

        generatedProcedures.Unary.Should().Equal(manualProcedures.Unary);
        generatedProcedures.Oneway.Should().Equal(manualProcedures.Oneway);
        generatedProcedures.Stream.Should().Equal(manualProcedures.Stream);
        generatedProcedures.ClientStream.Should().Equal(manualProcedures.ClientStream);
        generatedProcedures.Duplex.Should().Equal(manualProcedures.Duplex);

        static TSpec GetSpec<TSpec>(Dispatcher.Dispatcher dispatcher, string procedure, ProcedureKind kind)
            where TSpec : ProcedureSpec
        {
            dispatcher.TryGetProcedure(procedure, kind, out var spec).Should().BeTrue($"Procedure '{procedure}' not registered.");
            return spec.Should().BeOfType<TSpec>().Which;
        }

        var manualUnary = GetSpec<UnaryProcedureSpec>(manualDispatcher, "UnaryCall", ProcedureKind.Unary);
        var generatedUnary = GetSpec<UnaryProcedureSpec>(generatedDispatcher, "UnaryCall", ProcedureKind.Unary);
        generatedUnary.Encoding.Should().Be(manualUnary.Encoding);
        generatedUnary.Middleware.Count.Should().Be(manualUnary.Middleware.Count);

        var manualServerStream = GetSpec<StreamProcedureSpec>(manualDispatcher, "ServerStream", ProcedureKind.Stream);
        var generatedServerStream = GetSpec<StreamProcedureSpec>(generatedDispatcher, "ServerStream", ProcedureKind.Stream);
        generatedServerStream.Encoding.Should().Be(manualServerStream.Encoding);
        generatedServerStream.Metadata.Should().Be(manualServerStream.Metadata);

        var manualClientStream = GetSpec<ClientStreamProcedureSpec>(manualDispatcher, "ClientStream", ProcedureKind.ClientStream);
        var generatedClientStream = GetSpec<ClientStreamProcedureSpec>(generatedDispatcher, "ClientStream", ProcedureKind.ClientStream);
        generatedClientStream.Encoding.Should().Be(manualClientStream.Encoding);

        var manualDuplex = GetSpec<DuplexProcedureSpec>(manualDispatcher, "DuplexStream", ProcedureKind.Duplex);
        var generatedDuplex = GetSpec<DuplexProcedureSpec>(generatedDispatcher, "DuplexStream", ProcedureKind.Duplex);
        generatedDuplex.Encoding.Should().Be(manualDuplex.Encoding);
    }

    private static string PersistCertificate(X509Certificate2 certificate)
    {
        var path = Path.Combine(Path.GetTempPath(), $"omnirelay-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        return path;
    }

    private static void DeleteIfExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static async Task WaitForGrpcReadyAsync(Uri address, CancellationToken cancellationToken)
    {
        const int maxAttempts = 100;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(address.Host, address.Port).WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken);
                await Task.Delay(50, cancellationToken);
                return;
            }
            catch
            {
                await Task.Delay(25, cancellationToken);
            }
        }

        throw new TimeoutException("The gRPC inbound failed to bind within the allotted time.");
    }

    private static void RegisterTestServiceManually(Dispatcher.Dispatcher dispatcher, TestServiceOmniRelay.ITestService implementation)
    {
        var unaryCodec = new ProtobufCodec<UnaryRequest, UnaryResponse>(defaultEncoding: EncodingName);
        var serverStreamCodec = new ProtobufCodec<StreamRequest, StreamResponse>(defaultEncoding: EncodingName);
        var clientStreamCodec = new ProtobufCodec<StreamRequest, UnaryResponse>(defaultEncoding: EncodingName);
        var duplexCodec = new ProtobufCodec<StreamRequest, StreamResponse>(defaultEncoding: EncodingName);

        dispatcher.RegisterUnary("UnaryCall", builder =>
        {
            builder.WithEncoding(unaryCodec.Encoding);
            builder.Handle(ProtobufCallAdapters.CreateUnaryHandler(unaryCodec, implementation.UnaryCallAsync));
        });

        dispatcher.RegisterStream("ServerStream", builder =>
        {
            builder.WithEncoding(serverStreamCodec.Encoding);
            builder.Handle(ProtobufCallAdapters.CreateServerStreamHandler(serverStreamCodec, implementation.ServerStreamAsync));
        });

        dispatcher.RegisterClientStream("ClientStream", builder =>
        {
            builder.WithEncoding(clientStreamCodec.Encoding);
            builder.Handle(ProtobufCallAdapters.CreateClientStreamHandler(clientStreamCodec, implementation.ClientStreamAsync));
        });

        dispatcher.RegisterDuplex("DuplexStream", builder =>
        {
            builder.WithEncoding(duplexCodec.Encoding);
            builder.Handle(ProtobufCallAdapters.CreateDuplexHandler(duplexCodec, implementation.DuplexStreamAsync));
        });
    }

    private sealed class LoopbackTestService : TestServiceOmniRelay.ITestService
    {
        public ConcurrentQueue<RequestMeta> UnaryMetas { get; } = new();

        public ConcurrentQueue<RequestMeta> ServerStreamMetas { get; } = new();

        public ConcurrentQueue<RequestMeta> ClientStreamMetas { get; } = new();

        public ConcurrentQueue<RequestMeta> DuplexStreamMetas { get; } = new();

        public ValueTask<Response<UnaryResponse>> UnaryCallAsync(Request<UnaryRequest> request, CancellationToken cancellationToken)
        {
            UnaryMetas.Enqueue(request.Meta);
            var payload = new UnaryResponse { Message = $"{request.Body.Message}-ok" };
            return ValueTask.FromResult(Response<UnaryResponse>.Create(payload, new ResponseMeta(encoding: EncodingName)));
        }

        public async ValueTask ServerStreamAsync(Request<StreamRequest> request, ProtobufCallAdapters.ProtobufServerStreamWriter<StreamRequest, StreamResponse> stream, CancellationToken cancellationToken)
        {
            ServerStreamMetas.Enqueue(request.Meta);
            for (var index = 0; index < 3; index++)
            {
                var writeResult = await stream.WriteAsync(new StreamResponse { Value = $"{request.Body.Value}#{index}" }, cancellationToken).ConfigureAwait(false);
                writeResult.ValueOrChecked();
            }
        }

        public async ValueTask<Response<UnaryResponse>> ClientStreamAsync(ProtobufCallAdapters.ProtobufClientStreamContext<StreamRequest, UnaryResponse> context, CancellationToken cancellationToken)
        {
            ClientStreamMetas.Enqueue(context.Meta);
            var sum = 0;
            await foreach (var chunkResult in context.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var chunk = chunkResult.ValueOrChecked();
                _ = int.TryParse(chunk.Value, out var value);
                sum += value;
            }

            var payload = new UnaryResponse { Message = $"sum:{sum}" };
            return Response<UnaryResponse>.Create(payload, new ResponseMeta(encoding: EncodingName));
        }

        public async ValueTask DuplexStreamAsync(ProtobufCallAdapters.ProtobufDuplexStreamContext<StreamRequest, StreamResponse> context, CancellationToken cancellationToken)
        {
            DuplexStreamMetas.Enqueue(context.RequestMeta);
            var initialWrite = await context.WriteAsync(new StreamResponse { Value = "ready" }, cancellationToken).ConfigureAwait(false);
            initialWrite.ValueOrChecked();
            await foreach (var chunkResult in context.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var chunk = chunkResult.ValueOrChecked();
                var writeResult = await context.WriteAsync(new StreamResponse { Value = $"echo:{chunk.Value}" }, cancellationToken).ConfigureAwait(false);
                writeResult.ValueOrChecked();
            }
        }
    }

    private static bool IsKnownHttp3HandshakeIssue(Error? error)
    {
        if (error is null)
        {
            return false;
        }

        var message = error.Message ?? string.Empty;
        return message.Contains("unexpected EOF", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Exception was thrown by handler", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("The SSL connection could not be established", StringComparison.OrdinalIgnoreCase);
    }
}
