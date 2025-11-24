using System.Net;
using System.Net.Mime;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests.Support;
using OmniRelay.Transport.Http;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.IntegrationTests;

[Collection("CLI Integration")]
public sealed class CliToolingIntegrationTests
{
    [Fact(Timeout = 240_000)]
    public async ValueTask CliConfigValidateAndServe_StartsDispatcherFromScaffold()
    {
        using var tempDir = new TempDirectory();
        var configPath = TempDirectory.Resolve("appsettings.cli.json");
        var serviceName = $"cli-config-{Guid.NewGuid():N}";

        var scaffoldResult = await OmniRelayCliTestHelper.RunAsync(
            ["config", "scaffold", "--output", configPath, "--section", "omnirelay", "--service", serviceName],
            TestContext.Current.CancellationToken);
        scaffoldResult.ExitCode.Should().Be(0, scaffoldResult.StandardError);

        var httpPort = TestPortAllocator.GetRandomPort();
        var grpcPort = TestPortAllocator.GetRandomPort();
        var overrides = new[]
        {
            $"omnirelay:inbounds:http:0:urls:0=http://127.0.0.1:{httpPort}/",
            $"omnirelay:inbounds:grpc:0:urls:0=http://127.0.0.1:{grpcPort}"
        };

        var validateArgs = new List<string> { "config", "validate", "--config", configPath, "--section", "omnirelay" };
        foreach (var entry in overrides)
        {
            validateArgs.Add("--set");
            validateArgs.Add(entry);
        }

        var validateResult = await OmniRelayCliTestHelper.RunAsync(validateArgs, TestContext.Current.CancellationToken);
        validateResult.ExitCode.Should().Be(0, validateResult.StandardError);
        validateResult.StandardOutput.Should().Contain($"Configuration valid for service '{serviceName}'");

        var readyFile = TempDirectory.Resolve("ready.txt");
        var serveArgs = new List<string>
        {
            "serve",
            "--config",
            configPath,
            "--section",
            "omnirelay",
            "--ready-file",
            readyFile,
            "--shutdown-after",
            "00:00:10"
        };
        foreach (var entry in overrides)
        {
            serveArgs.Add("--set");
            serveArgs.Add(entry);
        }

        await using var serveProcess = OmniRelayCliTestHelper.StartBackground(serveArgs);
        await WaitForFileAsync(readyFile, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}/") };
        var healthResponse = await httpClient.GetAsync("healthz", TestContext.Current.CancellationToken);
        healthResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await serveProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
        var serveResult = await serveProcess.GetResultAsync(TestContext.Current.CancellationToken);
        serveResult.ExitCode.Should().Be(0, serveResult.StandardError);
    }

    [Fact(Timeout = 240_000)]
    public async ValueTask CliCommands_CanIntrospectRequestAndBenchmark_HttpHost()
    {
        var serviceName = $"cli-introspect-{Guid.NewGuid():N}";
        var httpPort = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{httpPort}/");

        var options = new DispatcherOptions(serviceName);
        var httpInbound = HttpInbound.TryCreate([baseAddress]).ValueOrChecked();
        options.AddLifecycle("cli-http", httpInbound);
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.Register(new UnaryProcedureSpec(
            serviceName,
            "cli::ping",
            static (request, _) =>
            {
                var input = Encoding.UTF8.GetString(request.Body.Span);
                var payload = Encoding.UTF8.GetBytes($"pong:{input}");
                var meta = new ResponseMeta(encoding: MediaTypeNames.Text.Plain, transport: "http");
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(payload, meta)));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsyncChecked(ct);

        try
        {
            var introspectResult = await OmniRelayCliTestHelper.RunAsync(
                ["introspect", "--url", $"{baseAddress}omnirelay/introspect", "--format", "json"],
                ct);
            introspectResult.ExitCode.Should().Be(0, introspectResult.StandardError);
            using (var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(introspectResult.StandardOutput)
                ? "{}"
                : introspectResult.StandardOutput))
            {
                document.RootElement.TryGetProperty("service", out var serviceProperty).Should().BeTrue();
                serviceProperty.GetString().Should().Be(serviceName);
            }

            var requestResult = await OmniRelayCliTestHelper.RunAsync(
                [
                    "request",
                    "--transport",
                    "http",
                    "--url",
                    baseAddress.ToString(),
                    "--service",
                    serviceName,
                    "--procedure",
                    "cli::ping",
                    "--encoding",
                    MediaTypeNames.Text.Plain,
                    "--body",
                    "hello-cli"
                ],
                ct);
            requestResult.ExitCode.Should().Be(0, requestResult.StandardError);
            requestResult.StandardOutput.Should().Contain("Request succeeded.");
            requestResult.StandardOutput.Should().Contain("pong:hello-cli");

            var benchmarkResult = await OmniRelayCliTestHelper.RunAsync(
                [
                    "benchmark",
                    "--transport",
                    "http",
                    "--url",
                    baseAddress.ToString(),
                    "--service",
                    serviceName,
                    "--procedure",
                    "cli::ping",
                    "--encoding",
                    MediaTypeNames.Text.Plain,
                    "--body",
                    "load-test",
                    "--concurrency",
                    "1",
                    "--requests",
                    "3"
                ],
                ct);
            benchmarkResult.ExitCode.Should().Be(0, benchmarkResult.StandardError);
            benchmarkResult.StandardOutput.Should().Contain("Benchmark complete.");
            benchmarkResult.StandardOutput.Should().Contain("Success: 3");
        }
        finally
        {
            await dispatcher.StopAsyncChecked(CancellationToken.None);
        }
    }

    [Http3Fact(Timeout = 150_000)]
    public async ValueTask CliHttp3Flags_EnableQuicListeners()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var tempDir = new TempDirectory();
        var configPath = TempDirectory.Resolve("appsettings.http3.json");
        var serviceName = $"cli-http3-{Guid.NewGuid():N}";
        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=cli-http3");
        var certificatePath = TempDirectory.Resolve("server-http3.pfx");
        File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx, "change-me"));

        var scaffoldResult = await OmniRelayCliTestHelper.RunAsync(
            [
                "config",
                "scaffold",
                "--output",
                configPath,
                "--section",
                "omnirelay",
                "--service",
                serviceName,
                "--http3-http",
                "--http3-grpc"
            ],
            TestContext.Current.CancellationToken);
        scaffoldResult.ExitCode.Should().Be(0, scaffoldResult.StandardError);

        var httpPort = TestPortAllocator.GetRandomPort();
        var http3Port = TestPortAllocator.GetRandomPort();
        var grpcPort = TestPortAllocator.GetRandomPort();
        var grpcHttp3Port = TestPortAllocator.GetRandomPort();

        var overrides = new[]
        {
            $"omnirelay:inbounds:http:0:urls:0=http://127.0.0.1:{httpPort}/",
            $"omnirelay:inbounds:http:1:urls:0=https://127.0.0.1:{http3Port}/",
            $"omnirelay:inbounds:http:1:tls:certificatePath={certificatePath}",
            "omnirelay:inbounds:http:1:tls:certificatePassword=change-me",
            $"omnirelay:inbounds:grpc:0:urls:0=http://127.0.0.1:{grpcPort}",
            $"omnirelay:inbounds:grpc:1:urls:0=https://127.0.0.1:{grpcHttp3Port}",
            $"omnirelay:inbounds:grpc:1:tls:certificatePath={certificatePath}",
            "omnirelay:inbounds:grpc:1:tls:certificatePassword=change-me"
        };

        var readyFile = TempDirectory.Resolve("ready-http3.txt");
        var serveArgs = new List<string>
        {
            "serve",
            "--config",
            configPath,
            "--section",
            "omnirelay",
            "--ready-file",
            readyFile,
            "--shutdown-after",
            "00:00:20"
        };

        foreach (var entry in overrides)
        {
            serveArgs.Add("--set");
            serveArgs.Add(entry);
        }

        await using var serveProcess = OmniRelayCliTestHelper.StartBackground(serveArgs);
        await WaitForFileAsync(readyFile, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        using (var client = CreateHttp3Client(new Uri($"https://127.0.0.1:{http3Port}/")))
        {
            var response = await client.GetAsync("healthz", TestContext.Current.CancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Version.Major.Should().Be(3);
        }

        await serveProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
        var serveResult = await serveProcess.GetResultAsync(TestContext.Current.CancellationToken);
        serveResult.ExitCode.Should().Be(0, serveResult.StandardError);
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!File.Exists(path))
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for file '{path}'.");
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private static HttpClient CreateHttp3Client(Uri baseAddress)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            EnableMultipleHttp3Connections = true,
            SslOptions =
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
                EnabledSslProtocols = SslProtocols.Tls13,
                ApplicationProtocols =
                [
                    SslApplicationProtocol.Http3,
                    SslApplicationProtocol.Http2,
                    SslApplicationProtocol.Http11
                ]
            }
        };

        var client = new HttpClient(handler) { BaseAddress = baseAddress };
        client.DefaultRequestVersion = HttpVersion.Version30;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        return client;
    }
}

[CollectionDefinition("CLI Integration", DisableParallelization = true)]
public sealed class CliIntegrationCollection : ICollectionFixture<CliIntegrationFixture>;

public sealed class CliIntegrationFixture;
