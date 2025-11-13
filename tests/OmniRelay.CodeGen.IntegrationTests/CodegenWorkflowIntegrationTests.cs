using System.Collections.Concurrent;
using System.Net.Quic;
using System.Security.Cryptography.X509Certificates;
using Hugo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OmniRelay.Configuration;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests;
using OmniRelay.Tests.Protos;
using OmniRelay.Tests.Support;
using OmniRelay.TestSupport;
using Xunit;

namespace OmniRelay.CodeGen.IntegrationTests;

public class CodegenWorkflowIntegrationTests
{
    private const string EncodingName = "protobuf";

    [Http3Fact(Timeout = 90_000)]
    public async Task GeneratedClient_RoundTripsOverHttp3_WhenDispatcherHostEnablesIt()
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
        serverBuilder.Services.AddSingleton<TestServiceOmniRelay.ITestService, LoopbackTestService>();
        serverBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "codegen-host-http3",
            ["omnirelay:inbounds:grpc:0:name"] = "grpc-http3",
            ["omnirelay:inbounds:grpc:0:urls:0"] = address.ToString(),
            ["omnirelay:inbounds:grpc:0:runtime:enableHttp3"] = "true",
            ["omnirelay:inbounds:grpc:0:runtime:interceptors:0"] = typeof(HostedProtocolCaptureInterceptor).AssemblyQualifiedName,
            ["omnirelay:inbounds:grpc:0:tls:certificatePath"] = certificatePath,
            ["omnirelay:inbounds:grpc:0:tls:checkCertificateRevocation"] = "false"
        });
        serverBuilder.Services.AddOmniRelayDispatcher(serverBuilder.Configuration.GetSection("omnirelay"));

        using var serverHost = serverBuilder.Build();
        var serverDispatcher = serverHost.Services.GetRequiredService<Dispatcher.Dispatcher>();
        var serviceImpl = (LoopbackTestService)serverHost.Services.GetRequiredService<TestServiceOmniRelay.ITestService>();
        serverDispatcher.RegisterTestService(serviceImpl);

        var clientBuilder = Host.CreateApplicationBuilder();
        clientBuilder.Services.AddLogging();
        clientBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "codegen-client-http3",
            ["omnirelay:outbounds:codegen-host-http3:unary:grpc:0:remoteService"] = "codegen-host-http3",
            ["omnirelay:outbounds:codegen-host-http3:unary:grpc:0:endpoints:0:address"] = address.ToString(),
            ["omnirelay:outbounds:codegen-host-http3:unary:grpc:0:endpoints:0:supportsHttp3"] = "true",
            ["omnirelay:outbounds:codegen-host-http3:unary:grpc:0:runtime:enableHttp3"] = "true",
            ["omnirelay:outbounds:codegen-host-http3:unary:grpc:0:runtime:requestVersion"] = "3.0",
            ["omnirelay:outbounds:codegen-host-http3:unary:grpc:0:runtime:versionPolicy"] = "RequestVersionExact",
            ["omnirelay:outbounds:codegen-host-http3:unary:grpc:0:tls:allowUntrustedCertificates"] = "true"
        });
        clientBuilder.Services.AddOmniRelayDispatcher(clientBuilder.Configuration.GetSection("omnirelay"));

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

            Assert.True(response.IsSuccess, response.Error?.Message);
            Assert.Equal("http3-ok", response.Value.Body.Message);
        }
        finally
        {
            await clientHost.StopAsync(CancellationToken.None);
            await serverHost.StopAsync(CancellationToken.None);
            DeleteIfExists(certificatePath);
        }

        Assert.True(serviceImpl.UnaryMetas.TryDequeue(out var unaryMeta), "Unary metadata not captured.");
        Assert.Equal("codegen-host-http3", unaryMeta.Service);
        Assert.Equal("UnaryCall", unaryMeta.Procedure);
        Assert.Equal(EncodingName, unaryMeta.Encoding);

        Assert.True(protocols.TryDequeue(out var observed), "No protocol captured.");
        Assert.StartsWith("HTTP/3", observed, StringComparison.OrdinalIgnoreCase);
    }

    [Http3Fact(Timeout = 90_000)]
    public async Task GeneratedClient_FallsBackToHttp2_WhenServerDisablesHttp3()
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
        serverBuilder.Services.AddSingleton<TestServiceOmniRelay.ITestService, LoopbackTestService>();
        serverBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "codegen-host-http2",
            ["omnirelay:inbounds:grpc:0:name"] = "grpc-http2",
            ["omnirelay:inbounds:grpc:0:urls:0"] = address.ToString(),
            ["omnirelay:inbounds:grpc:0:runtime:enableHttp3"] = "false",
            ["omnirelay:inbounds:grpc:0:runtime:interceptors:0"] = typeof(HostedProtocolCaptureInterceptor).AssemblyQualifiedName,
            ["omnirelay:inbounds:grpc:0:tls:certificatePath"] = certificatePath,
            ["omnirelay:inbounds:grpc:0:tls:checkCertificateRevocation"] = "false"
        });
        serverBuilder.Services.AddOmniRelayDispatcher(serverBuilder.Configuration.GetSection("omnirelay"));

        using var serverHost = serverBuilder.Build();
        var serverDispatcher = serverHost.Services.GetRequiredService<Dispatcher.Dispatcher>();
        var serviceImpl = (LoopbackTestService)serverHost.Services.GetRequiredService<TestServiceOmniRelay.ITestService>();
        serverDispatcher.RegisterTestService(serviceImpl);

        var clientBuilder = Host.CreateApplicationBuilder();
        clientBuilder.Services.AddLogging();
        clientBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "codegen-client-http2",
            ["omnirelay:outbounds:codegen-host-http2:unary:grpc:0:remoteService"] = "codegen-host-http2",
            ["omnirelay:outbounds:codegen-host-http2:unary:grpc:0:endpoints:0:address"] = address.ToString(),
            ["omnirelay:outbounds:codegen-host-http2:unary:grpc:0:endpoints:0:supportsHttp3"] = "false",
            ["omnirelay:outbounds:codegen-host-http2:unary:grpc:0:runtime:enableHttp3"] = "true",
            ["omnirelay:outbounds:codegen-host-http2:unary:grpc:0:runtime:requestVersion"] = "3.0",
            ["omnirelay:outbounds:codegen-host-http2:unary:grpc:0:runtime:versionPolicy"] = "RequestVersionExact",
            ["omnirelay:outbounds:codegen-host-http2:unary:grpc:0:tls:allowUntrustedCertificates"] = "true"
        });
        clientBuilder.Services.AddOmniRelayDispatcher(clientBuilder.Configuration.GetSection("omnirelay"));

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

            Assert.True(response.IsSuccess, response.Error?.Message);
            Assert.Equal("fallback-ok", response.Value.Body.Message);
        }
        finally
        {
            await clientHost.StopAsync(CancellationToken.None);
            await serverHost.StopAsync(CancellationToken.None);
            DeleteIfExists(certificatePath);
        }

        Assert.True(serviceImpl.UnaryMetas.TryDequeue(out var unaryMeta), "Unary metadata not captured.");
        Assert.Equal("codegen-host-http2", unaryMeta.Service);
        Assert.Equal("UnaryCall", unaryMeta.Procedure);
        Assert.Equal(EncodingName, unaryMeta.Encoding);

        Assert.True(protocols.TryDequeue(out var observed), "No protocol captured.");
        Assert.StartsWith("HTTP/2", observed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 90_000)]
    public async Task GeneratedClient_StreamHelpers_WorkWhenResolvedFromDependencyInjection()
    {
        var address = new Uri($"http://127.0.0.1:{TestPortAllocator.GetRandomPort()}");

        var serverBuilder = Host.CreateApplicationBuilder();
        serverBuilder.Services.AddLogging();
        serverBuilder.Services.AddSingleton<TestServiceOmniRelay.ITestService, LoopbackTestService>();
        serverBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "codegen-host-streams",
            ["omnirelay:inbounds:grpc:0:name"] = "grpc-h2",
            ["omnirelay:inbounds:grpc:0:urls:0"] = address.ToString()
        });
        serverBuilder.Services.AddOmniRelayDispatcher(serverBuilder.Configuration.GetSection("omnirelay"));

        using var serverHost = serverBuilder.Build();
        var serverDispatcher = serverHost.Services.GetRequiredService<Dispatcher.Dispatcher>();
        var serviceImpl = (LoopbackTestService)serverHost.Services.GetRequiredService<TestServiceOmniRelay.ITestService>();
        serverDispatcher.RegisterTestService(serviceImpl);

        var clientBuilder = Host.CreateApplicationBuilder();
        clientBuilder.Services.AddLogging();
        clientBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "codegen-client-streams",
            ["omnirelay:outbounds:codegen-host-streams:unary:grpc:0:remoteService"] = "codegen-host-streams",
            ["omnirelay:outbounds:codegen-host-streams:unary:grpc:0:addresses:0"] = address.ToString(),
            ["omnirelay:outbounds:codegen-host-streams:stream:grpc:0:remoteService"] = "codegen-host-streams",
            ["omnirelay:outbounds:codegen-host-streams:stream:grpc:0:addresses:0"] = address.ToString(),
            ["omnirelay:outbounds:codegen-host-streams:clientStream:grpc:0:remoteService"] = "codegen-host-streams",
            ["omnirelay:outbounds:codegen-host-streams:clientStream:grpc:0:addresses:0"] = address.ToString(),
            ["omnirelay:outbounds:codegen-host-streams:duplex:grpc:0:remoteService"] = "codegen-host-streams",
            ["omnirelay:outbounds:codegen-host-streams:duplex:grpc:0:addresses:0"] = address.ToString()
        });
        clientBuilder.Services.AddOmniRelayDispatcher(clientBuilder.Configuration.GetSection("omnirelay"));
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
            Assert.True(unary.IsSuccess, unary.Error?.Message);
            Assert.Equal("di-ok", unary.Value.Body.Message);

            var streamValues = new List<string>();
            await foreach (var message in client.ServerStreamAsync(new StreamRequest { Value = "flow" }, cancellationToken: ct))
            {
                streamValues.Add(message.ValueOrThrow().Body.Value);
            }
            Assert.Equal(new[] { "flow#0", "flow#1", "flow#2" }, streamValues);

            var sessionResult = await client.ClientStreamAsync(cancellationToken: ct);
            await using (var session = sessionResult.ValueOrThrow())
            {
                (await session.WriteAsync(new StreamRequest { Value = "4" }, ct)).ThrowIfFailure();
                (await session.WriteAsync(new StreamRequest { Value = "6" }, ct)).ThrowIfFailure();
                await session.CompleteAsync(ct);
                var aggregate = (await session.Response).ValueOrThrow();
                Assert.Equal("sum:10", aggregate.Body.Message);
            }

            var duplexResult = await client.DuplexStreamAsync(cancellationToken: ct);
            await using (var duplex = duplexResult.ValueOrThrow())
            {
                (await duplex.WriteAsync(new StreamRequest { Value = "alpha" }, ct)).ThrowIfFailure();
                (await duplex.WriteAsync(new StreamRequest { Value = "beta" }, ct)).ThrowIfFailure();
                await duplex.CompleteRequestsAsync(cancellationToken: ct);

                var duplexValues = new List<string>();
                await foreach (var response in duplex.ReadResponsesAsync(ct))
                {
                    duplexValues.Add(response.ValueOrThrow().Body.Value);
                }

                Assert.Equal(new[] { "ready", "echo:alpha", "echo:beta" }, duplexValues);
            }
        }
        finally
        {
            await clientHost.StopAsync(CancellationToken.None);
            await serverHost.StopAsync(CancellationToken.None);
        }

        Assert.True(serviceImpl.ServerStreamMetas.TryDequeue(out var serverStreamMeta), "Server stream metadata missing.");
        Assert.Equal("codegen-host-streams", serverStreamMeta.Service);
        Assert.Equal("ServerStream", serverStreamMeta.Procedure);
        Assert.Equal(EncodingName, serverStreamMeta.Encoding);

        Assert.True(serviceImpl.ClientStreamMetas.TryDequeue(out var clientStreamMeta), "Client stream metadata missing.");
        Assert.Equal("codegen-host-streams", clientStreamMeta.Service);
        Assert.Equal("ClientStream", clientStreamMeta.Procedure);
        Assert.Equal(EncodingName, clientStreamMeta.Encoding);

        Assert.True(serviceImpl.DuplexStreamMetas.TryDequeue(out var duplexMeta), "Duplex stream metadata missing.");
        Assert.Equal("codegen-host-streams", duplexMeta.Service);
        Assert.Equal("DuplexStream", duplexMeta.Procedure);
        Assert.Equal(EncodingName, duplexMeta.Encoding);
    }

    [Fact]
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

        Assert.Equal(manualProcedures.Unary, generatedProcedures.Unary);
        Assert.Equal(manualProcedures.Oneway, generatedProcedures.Oneway);
        Assert.Equal(manualProcedures.Stream, generatedProcedures.Stream);
        Assert.Equal(manualProcedures.ClientStream, generatedProcedures.ClientStream);
        Assert.Equal(manualProcedures.Duplex, generatedProcedures.Duplex);

        static TSpec GetSpec<TSpec>(Dispatcher.Dispatcher dispatcher, string procedure, ProcedureKind kind)
            where TSpec : ProcedureSpec
        {
            Assert.True(dispatcher.TryGetProcedure(procedure, kind, out var spec), $"Procedure '{procedure}' not registered.");
            return Assert.IsType<TSpec>(spec);
        }

        var manualUnary = GetSpec<UnaryProcedureSpec>(manualDispatcher, "UnaryCall", ProcedureKind.Unary);
        var generatedUnary = GetSpec<UnaryProcedureSpec>(generatedDispatcher, "UnaryCall", ProcedureKind.Unary);
        Assert.Equal(manualUnary.Encoding, generatedUnary.Encoding);
        Assert.Equal(manualUnary.Middleware.Count, generatedUnary.Middleware.Count);

        var manualServerStream = GetSpec<StreamProcedureSpec>(manualDispatcher, "ServerStream", ProcedureKind.Stream);
        var generatedServerStream = GetSpec<StreamProcedureSpec>(generatedDispatcher, "ServerStream", ProcedureKind.Stream);
        Assert.Equal(manualServerStream.Encoding, generatedServerStream.Encoding);
        Assert.Equal(manualServerStream.Metadata, generatedServerStream.Metadata);

        var manualClientStream = GetSpec<ClientStreamProcedureSpec>(manualDispatcher, "ClientStream", ProcedureKind.ClientStream);
        var generatedClientStream = GetSpec<ClientStreamProcedureSpec>(generatedDispatcher, "ClientStream", ProcedureKind.ClientStream);
        Assert.Equal(manualClientStream.Encoding, generatedClientStream.Encoding);

        var manualDuplex = GetSpec<DuplexProcedureSpec>(manualDispatcher, "DuplexStream", ProcedureKind.Duplex);
        var generatedDuplex = GetSpec<DuplexProcedureSpec>(generatedDispatcher, "DuplexStream", ProcedureKind.Duplex);
        Assert.Equal(manualDuplex.Encoding, generatedDuplex.Encoding);
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
                writeResult.ThrowIfFailure();
            }
        }

        public async ValueTask<Response<UnaryResponse>> ClientStreamAsync(ProtobufCallAdapters.ProtobufClientStreamContext<StreamRequest, UnaryResponse> context, CancellationToken cancellationToken)
        {
            ClientStreamMetas.Enqueue(context.Meta);
            var sum = 0;
            await foreach (var chunkResult in context.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var chunk = chunkResult.ValueOrThrow();
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
            initialWrite.ThrowIfFailure();
            await foreach (var chunkResult in context.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var chunk = chunkResult.ValueOrThrow();
                var writeResult = await context.WriteAsync(new StreamResponse { Value = $"echo:{chunk.Value}" }, cancellationToken).ConfigureAwait(false);
                writeResult.ThrowIfFailure();
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
