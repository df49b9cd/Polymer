using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Hugo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests;
using OmniRelay.TestSupport;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;
using OmniRelay.YabInterop.Protos;
using Xunit;
using Xunit.Sdk;
using Yarp.ReverseProxy;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using static Hugo.Go;

namespace OmniRelay.IntegrationTests;

public sealed class CompatibilityInteropIntegrationTests
{
    [Fact(Timeout = 90_000)]
    public async Task YabHttp11Interop_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var yabPath = ExternalTool.Require("yab", "yab CLI missing. Install go.uber.org/yarpc/yab to run this test.");

        var serviceName = $"compat-http-{Guid.NewGuid():N}";
        const string procedure = "compat::ping";
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var payloadCapture = CreateCompletionSource<string>();
        var protocolCapture = CreateCompletionSource<string>();
        var callerCapture = CreateCompletionSource<string?>();

        var dispatcher = CreateHttpDispatcher(
            serviceName,
            baseAddress,
            procedure,
            (request, _) =>
            {
                var body = Encoding.UTF8.GetString(request.Body.Span);
                payloadCapture.TrySetResult(body);
                protocolCapture.TrySetResult(request.Meta.Headers.TryGetValue(HttpTransportHeaders.Protocol, out var protocol) ? protocol : "missing");
                callerCapture.TrySetResult(request.Meta.Caller);

                var responseBytes = "{\"message\":\"interop\"}"u8.ToArray();
                var meta = new ResponseMeta(encoding: MediaTypeNames.Application.Json);
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(responseBytes, meta)));
            });

        await dispatcher.StartOrThrowAsync(ct);

        try
        {
            var requestJson = "{\"message\":\"interop-yab\"}";
            var args = new[]
            {
                "--http",
                "--peer", baseAddress.ToString(),
                "--service", serviceName,
                "--procedure", procedure,
                "--encoding", "json",
                "--request", requestJson,
                "--caller", "compat-yab",
                "--timeout", "2s"
            };

            var result = await ProcessRunner.RunAsync(
                yabPath,
                args,
                TimeSpan.FromSeconds(20),
                RepositoryPaths.Root,
                cancellationToken: ct);

            Assert.True(result.ExitCode == 0, $"yab invocation failed: {result.StandardError}");

            var body = await payloadCapture.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("interop-yab", json.RootElement.GetProperty("message").GetString());

            var protocol = await protocolCapture.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("HTTP/1.1", protocol);

            var caller = await callerCapture.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("compat-yab", caller);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task GrpcurlInterop_UsesHttp2()
    {
        var ct = TestContext.Current.CancellationToken;
        var grpcurlPath = ExternalTool.Require("grpcurl", "grpcurl CLI missing. Install github.com/fullstorydev/grpcurl to run this test.");

        var serviceName = "echo.EchoService";
        const string procedure = "Ping";
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var requestCapture = CreateCompletionSource<EchoRequest>();
        var protocolCapture = CreateCompletionSource<string>();

        var options = new DispatcherOptions(serviceName);
        var inbound = new GrpcInbound([address.ToString()]);
        options.AddLifecycle("compat-grpc", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            serviceName,
            procedure,
            (request, _) =>
            {
                var message = EchoRequest.Parser.ParseFrom(request.Body.ToArray());
                requestCapture.TrySetResult(message);
                protocolCapture.TrySetResult(request.Meta.Headers.TryGetValue(HttpTransportHeaders.Protocol, out var protocol) ? protocol : "missing");

                var response = new EchoResponse { Message = $"{message.Message}-grpcurl" };
                var meta = new ResponseMeta(encoding: "application/grpc");
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(response.ToByteArray(), meta)));
            }));

        await dispatcher.StartOrThrowAsync(ct);
        await WaitForTcpEndpointAsync(address, ct);

        try
        {
            var protoDir = Path.Combine(RepositoryPaths.Root, "tests", "OmniRelay.YabInterop", "Protos");
            var args = new[]
            {
                "-plaintext",
                "-import-path", protoDir,
                "-proto", "echo.proto",
                "-d", "{\"message\":\"grpcurl\"}",
                $"{address.Host}:{address.Port}",
                "echo.EchoService/Ping"
            };

            var result = await ProcessRunner.RunAsync(
                grpcurlPath,
                args,
                TimeSpan.FromSeconds(20),
                RepositoryPaths.Root,
                cancellationToken: ct);

            Assert.True(result.ExitCode == 0, $"grpcurl failed: {result.StandardError}");

            var request = await requestCapture.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("grpcurl", request.Message);

            var protocol = await protocolCapture.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("HTTP/2", protocol);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    [Http3Fact(Timeout = 120_000)]
    public async Task CurlHttp3Interop_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var curlPath = ExternalTool.Require("curl", "curl (with HTTP/3 support) is required for this test.");
        await EnsureCurlSupportsHttp3Async(curlPath, ct);

        var serviceName = $"compat-http3-{Guid.NewGuid():N}";
        const string procedure = "compat::curl";
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"https://127.0.0.1:{port}/");
        using var certificate = TestCertificateFactory.CreateLoopbackCertificate($"CN={serviceName}");

        var protocolCapture = CreateCompletionSource<string>();

        var dispatcher = CreateHttpDispatcher(
            serviceName,
            baseAddress,
            procedure,
            (request, _) =>
            {
                protocolCapture.TrySetResult(request.Meta.Headers.TryGetValue(HttpTransportHeaders.Protocol, out var protocol) ? protocol : "missing");
                var responseBytes = "{\"message\":\"curl-http3\"}"u8.ToArray();
                var meta = new ResponseMeta(encoding: MediaTypeNames.Application.Json);
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(responseBytes, meta)));
            },
            new HttpServerRuntimeOptions { EnableHttp3 = true },
            new HttpServerTlsOptions { Certificate = certificate });

        await dispatcher.StartOrThrowAsync(ct);

        try
        {
            var supportsHttp3Only = await CurlSupportsHttp3OnlyFlagAsync(curlPath, ct);
            var requestBody = "{\"message\":\"curl-http3\"}";
            var args = new List<string>
            {
                supportsHttp3Only ? "--http3-only" : "--http3",
                "--insecure",
                "--silent",
                "--show-error",
                "--request", "POST",
                baseAddress.ToString(),
                "-H", $"{HttpTransportHeaders.Procedure}: {procedure}",
                "-H", $"{HttpTransportHeaders.Encoding}: json",
                "-H", "Content-Type: application/json",
                "-H", $"{HttpTransportHeaders.Transport}: http",
                "--data", requestBody
            };

            var result = await ProcessRunner.RunAsync(
                curlPath,
                args,
                TimeSpan.FromSeconds(20),
                RepositoryPaths.Root,
                cancellationToken: ct);

            Assert.True(result.ExitCode == 0, $"curl HTTP/3 call failed: {result.StandardError}");

            var protocol = await protocolCapture.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("HTTP/3", protocol);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task YarpProxy_ForwardsHeadersAndHttp2Negotiation()
    {
        var ct = TestContext.Current.CancellationToken;

        var serviceName = $"compat-yarp-{Guid.NewGuid():N}";
        const string procedure = "compat::proxy";
        var backendPort = TestPortAllocator.GetRandomPort();
        var backendAddress = new Uri($"http://127.0.0.1:{backendPort}/");

        var protocolCapture = CreateCompletionSource<string>();
        var callerCapture = CreateCompletionSource<string?>();

        var backendDispatcher = CreateHttpDispatcher(
            serviceName,
            backendAddress,
            procedure,
            (request, _) =>
            {
                protocolCapture.TrySetResult(request.Meta.Headers.TryGetValue(HttpTransportHeaders.Protocol, out var protocol) ? protocol : "missing");
                callerCapture.TrySetResult(request.Meta.Caller);

                var body = "{\"message\":\"proxy-ok\"}"u8.ToArray();
                var meta = new ResponseMeta(encoding: MediaTypeNames.Application.Json);
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(body, meta)));
            });

        await backendDispatcher.StartOrThrowAsync(ct);

        var proxyPort = TestPortAllocator.GetRandomPort();
        var routes = new[]
        {
            new RouteConfig
            {
                RouteId = "omnirelay",
                ClusterId = "backend",
                Match = new RouteMatch { Path = "/{**catch-all}" }
            }
        };
        var clusters = new[]
        {
            new ClusterConfig
            {
                ClusterId = "backend",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["primary"] = new() { Address = backendAddress.ToString() }
                },
                HttpRequest = new ForwarderRequestConfig
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                }
            }
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, proxyPort, listen => listen.Protocols = HttpProtocols.Http2);
        });

        builder.Services.AddReverseProxy().LoadFromMemory(routes, clusters);
        var proxyApp = builder.Build();
        proxyApp.MapReverseProxy();
        await proxyApp.StartAsync(ct);

        try
        {
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None
            };
            handler.EnableMultipleHttp2Connections = true;

            using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{proxyPort}/") };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/");
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Content = new StringContent("{\"message\":\"proxy\"}", Encoding.UTF8, MediaTypeNames.Application.Json);
            request.Headers.Add(HttpTransportHeaders.Procedure, procedure);
            request.Headers.Add(HttpTransportHeaders.Encoding, "json");
            request.Headers.Add(HttpTransportHeaders.Transport, "http");
            request.Headers.Add(HttpTransportHeaders.Caller, "compat-yarp-client");

            using var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, response.Version.Major);

            var protocol = await protocolCapture.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("HTTP/1.1", protocol);

            var caller = await callerCapture.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("compat-yarp-client", caller);
        }
        finally
        {
            await proxyApp.StopAsync(ct);
            await backendDispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task EnvoyProxy_ForwardsRpcHeadersAndNegotiatesHttp2()
    {
        var ct = TestContext.Current.CancellationToken;
        if (!string.Equals(Environment.GetEnvironmentVariable("OMNIRELAY_ENABLE_ENVOY_TESTS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("Set OMNIRELAY_ENABLE_ENVOY_TESTS=true to exercise the Envoy proxy scenario.");
        }

        var dockerPath = await DockerHelper.RequireAsync(ct);
        var envoyImage = Environment.GetEnvironmentVariable("OMNIRELAY_ENVOY_IMAGE") ?? "envoyproxy/envoy:v1.30-latest";

        var serviceName = $"compat-envoy-{Guid.NewGuid():N}";
        const string procedure = "compat::proxy";
        var backendPort = TestPortAllocator.GetRandomPort();
        var backendAddress = new Uri($"http://127.0.0.1:{backendPort}/");
        var protocolCapture = CreateCompletionSource<string>();
        var callerCapture = CreateCompletionSource<string?>();

        var backendDispatcher = CreateHttpDispatcher(
            serviceName,
            backendAddress,
            procedure,
            (request, _) =>
            {
                protocolCapture.TrySetResult(request.Meta.Headers.TryGetValue(HttpTransportHeaders.Protocol, out var protocol) ? protocol : "missing");
                callerCapture.TrySetResult(request.Meta.Caller);
                var body = "{\"message\":\"envoy-ok\"}"u8.ToArray();
                var meta = new ResponseMeta(encoding: MediaTypeNames.Application.Json);
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(body, meta)));
            });

        await backendDispatcher.StartOrThrowAsync(ct);

        using var configDir = new TempDirectory();
        var configPath = TempDirectory.Resolve("envoy.yaml");
        await File.WriteAllTextAsync(configPath, BuildEnvoyConfig(backendPort), Encoding.UTF8, ct);

        var proxyPort = TestPortAllocator.GetRandomPort();
        using var dockerProcess = StartBackgroundProcess(
            dockerPath,
            [
                "run",
                "--rm",
                "-v", $"{TempDirectory.Path}:/etc/envoy",
                "-p", $"{proxyPort}:10000",
                envoyImage,
                "-c", "/etc/envoy/envoy.yaml"
            ]);

        try
        {
            try
            {
                await WaitForTcpEndpointAsync(
                    new Uri($"http://127.0.0.1:{proxyPort}"),
                    ct,
                    maxAttempts: 600,
                    retryDelayMilliseconds: 200,
                    settleDelayMilliseconds: 200);
            }
            catch (TimeoutException)
            {
                Assert.Skip("Envoy container did not expose the listener within the allotted time.");
            }

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{proxyPort}/") };
            using var response = await SendWithRetriesAsync(
                client,
                procedure,
                "compat-envoy-client",
                "{\"message\":\"envoy\"}",
                ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var protocol = await protocolCapture.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Equal("HTTP/2", protocol);

            var caller = await callerCapture.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Equal("compat-envoy-client", caller);
        }
        finally
        {
            TryKill(dockerProcess);
            dockerProcess.Dispose();
            await backendDispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task TeeOutbound_RoutesShadowTrafficDuringRollingUpgrade()
    {
        var ct = TestContext.Current.CancellationToken;

        var frontPort = TestPortAllocator.GetRandomPort();
        var frontAddress = new Uri($"http://127.0.0.1:{frontPort}/");

        var primaryCapture = CreateCompletionSource<RequestMeta>();
        var shadowCapture = CreateCompletionSource<RequestMeta>();

        var primaryOutbound = new CapturingUnaryOutbound("primary", primaryCapture);
        var shadowOutbound = new CapturingUnaryOutbound("shadow", shadowCapture);

        var teeOptions = new TeeOptions
        {
            SampleRate = 1.0,
            ShadowHeaderName = "x-shadow-route",
            ShadowHeaderValue = "beta-canary"
        };

        var frontOptions = new DispatcherOptions("compat.shadow");
        var inbound = new HttpInbound([frontAddress.ToString()]);
        frontOptions.AddLifecycle("front-http", inbound);
        frontOptions.AddTeeUnaryOutbound("rolling-upgrade", null, primaryOutbound, shadowOutbound, teeOptions);

        var frontDispatcher = new OmniRelay.Dispatcher.Dispatcher(frontOptions);
        var upstreamClient = frontDispatcher.CreateJsonClient<ShadowPingRequest, ShadowPingResponse>(
            "rolling-upgrade",
            "shadow::check",
            builder =>
            {
                builder.Encoding = MediaTypeNames.Application.Json;
                builder.SerializerContext = CompatibilityInteropJsonContext.Default;
            });

        frontDispatcher.RegisterJsonUnary<ShadowPingRequest, ShadowPingResponse>(
            "rolling::ping",
            async (context, request) =>
            {
                var outboundMeta = new RequestMeta(
                    service: "rolling-upgrade",
                    procedure: "shadow::check",
                    caller: context.Meta.Caller ?? frontDispatcher.ServiceName,
                    transport: "http",
                    headers:
                    [
                        new KeyValuePair<string, string>("x-upgrade-phase", request.Phase)
                    ]);

                var upstream = await upstreamClient.CallAsync(new Request<ShadowPingRequest>(outboundMeta, request), context.CancellationToken);
                if (upstream.IsFailure)
                {
                    var error = upstream.Error ?? OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Internal, "Primary cluster failed.", context.Meta.Transport ?? "http");
                    throw new OmniRelayException(OmniRelayErrorAdapter.ToStatus(error), error.Message, error, context.Meta.Transport ?? "http");
                }

                return Response<ShadowPingResponse>.Create(upstream.Value.Body, upstream.Value.Meta);
            },
            builder =>
            {
                builder.Encoding = MediaTypeNames.Application.Json;
                builder.SerializerContext = CompatibilityInteropJsonContext.Default;
            });

        await frontDispatcher.StartOrThrowAsync(ct);

        try
        {
            using var client = new HttpClient { BaseAddress = frontAddress };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/");
            request.Headers.Add(HttpTransportHeaders.Procedure, "rolling::ping");
            request.Headers.Add(HttpTransportHeaders.Encoding, "json");
            request.Headers.Add(HttpTransportHeaders.Transport, "http");
            request.Headers.Add(HttpTransportHeaders.Caller, "compat-shadow-client");
            request.Content = new StringContent("{\"phase\":\"beta\"}", Encoding.UTF8, MediaTypeNames.Application.Json);

            var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var payload = await response.Content.ReadAsByteArrayAsync(ct);
            var responseBody = JsonSerializer.Deserialize(
                payload,
                CompatibilityInteropJsonContext.Default.ShadowPingResponse);
            Assert.Equal("primary", responseBody?.Cluster);

            var shadowMeta = await shadowCapture.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("beta-canary", shadowMeta.Headers["x-shadow-route"]);

            var primaryMeta = await primaryCapture.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("compat-shadow-client", primaryMeta.Caller);
            Assert.Equal("beta", primaryMeta.Headers["x-upgrade-phase"]);
        }
        finally
        {
            await frontDispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    private static OmniRelay.Dispatcher.Dispatcher CreateHttpDispatcher(
        string serviceName,
        Uri baseAddress,
        string procedure,
        UnaryInboundDelegate handler,
        HttpServerRuntimeOptions? runtimeOptions = null,
        HttpServerTlsOptions? tlsOptions = null)
    {
        var options = new DispatcherOptions(serviceName);
        var inbound = new HttpInbound([baseAddress.ToString()], serverRuntimeOptions: runtimeOptions, serverTlsOptions: tlsOptions);
        options.AddLifecycle($"{serviceName}-http", inbound);
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.Register(new UnaryProcedureSpec(
            serviceName,
            procedure,
            handler));

        return dispatcher;
    }

    private static TaskCompletionSource<T> CreateCompletionSource<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitForTcpEndpointAsync(
        Uri address,
        CancellationToken cancellationToken,
        int maxAttempts = 100,
        int retryDelayMilliseconds = 50,
        int settleDelayMilliseconds = 50)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address.Host, address.Port, cancellationToken);
                if (settleDelayMilliseconds > 0)
                {
                    await Task.Delay(settleDelayMilliseconds, cancellationToken);
                }
                return;
            }
            catch
            {
                await Task.Delay(retryDelayMilliseconds, cancellationToken);
            }
        }

        throw new TimeoutException($"Endpoint {address} did not accept connections.");
    }

    private static async Task EnsureCurlSupportsHttp3Async(string curlPath, CancellationToken cancellationToken)
    {
        var version = await ProcessRunner.RunAsync(
            curlPath,
            ["--version"],
            TimeSpan.FromSeconds(5),
            RepositoryPaths.Root,
            cancellationToken: cancellationToken);

        if (version.ExitCode != 0 || !version.StandardOutput.Contains("HTTP3", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("curl build does not include HTTP/3 support.");
        }
    }

    private static async Task<bool> CurlSupportsHttp3OnlyFlagAsync(string curlPath, CancellationToken cancellationToken)
    {
        var help = await ProcessRunner.RunAsync(
            curlPath,
            ["--help"],
            TimeSpan.FromSeconds(5),
            RepositoryPaths.Root,
            cancellationToken: cancellationToken);

        return help.ExitCode == 0 && help.StandardOutput.Contains("--http3-only", StringComparison.Ordinal);
    }

    private static Process StartBackgroundProcess(string fileName, IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = RepositoryPaths.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        }

        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    private static string BuildEnvoyConfig(int backendPort) =>
        $$"""
        static_resources:
          listeners:
          - name: ingress_listener
            address:
              socket_address:
                address: 0.0.0.0
                port_value: 10000
            filter_chains:
            - filters:
              - name: envoy.filters.network.http_connection_manager
                typed_config:
                  "@type": type.googleapis.com/envoy.extensions.filters.network.http_connection_manager.v3.HttpConnectionManager
                  stat_prefix: ingress_http
                  codec_type: AUTO
                  http2_protocol_options: {}
                  route_config:
                    name: local_route
                    virtual_hosts:
                    - name: backend
                      domains: ["*"]
                      routes:
                      - match: { prefix: "/" }
                        route:
                          cluster: omnirelay
                  upgrade_configs:
                  - upgrade_type: websocket
          clusters:
          - name: omnirelay
            connect_timeout: 1s
            type: LOGICAL_DNS
            http2_protocol_options: {}
            load_assignment:
              cluster_name: omnirelay
              endpoints:
              - lb_endpoints:
                - endpoint:
                    address:
                      socket_address:
                        address: host.docker.internal
                        port_value: {{backendPort}}
        admin:
          address:
            socket_address:
              address: 0.0.0.0
              port_value: 9901
        """;

    private static async Task<HttpResponseMessage> SendWithRetriesAsync(
        HttpClient client,
        string procedure,
        string caller,
        string body,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/");
            request.Headers.Add(HttpTransportHeaders.Procedure, procedure);
            request.Headers.Add(HttpTransportHeaders.Encoding, "json");
            request.Headers.Add(HttpTransportHeaders.Transport, "http");
            request.Headers.Add(HttpTransportHeaders.Caller, caller);
            request.Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json);

            try
            {
                return await client.SendAsync(request, cancellationToken);
            }
            catch when (attempt < 9)
            {
                await Task.Delay(200, cancellationToken);
            }
        }

        throw new InvalidOperationException("Envoy proxy was not reachable after multiple attempts.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}

internal sealed class CapturingUnaryOutbound : IUnaryOutbound
{
    private readonly string _clusterLabel;

    public CapturingUnaryOutbound(string clusterLabel, TaskCompletionSource<RequestMeta> capture)
    {
        _clusterLabel = clusterLabel;
        Capture = capture;
    }

    public TaskCompletionSource<RequestMeta> Capture { get; }

    public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default)
    {
        Capture.TrySetResult(request.Meta);
        var shadowHeader = request.Meta.Headers.TryGetValue("x-shadow-route", out var header)
            ? header
            : "absent";

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new ShadowPingResponse(_clusterLabel, shadowHeader),
            CompatibilityInteropJsonContext.Default.ShadowPingResponse);

        var meta = new ResponseMeta(encoding: MediaTypeNames.Application.Json);
        return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(payload, meta)));
    }
}

internal sealed record ShadowPingRequest(string Phase);

internal sealed record ShadowPingResponse(string Cluster, string ShadowFlag);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ShadowPingRequest))]
[JsonSerializable(typeof(ShadowPingResponse))]
internal partial class CompatibilityInteropJsonContext : JsonSerializerContext;
