using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.TestSupport;
using OmniRelay.Tests;
using OmniRelay.Transport.Http;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.IntegrationTests;

[Collection("CLI Integration")]
public sealed class CliToolingIntegrationTests
{
    [Fact(Timeout = 240_000)]
    public async Task CliConfigValidateAndServe_StartsDispatcherFromScaffold()
    {
        using var tempDir = new TempDirectory();
        var configPath = tempDir.Resolve("appsettings.cli.json");
        var serviceName = $"cli-config-{Guid.NewGuid():N}";

        var scaffoldResult = await OmniRelayCliTestHelper.RunAsync(
            ["config", "scaffold", "--output", configPath, "--section", "omnirelay", "--service", serviceName],
            TestContext.Current.CancellationToken);
        Assert.True(scaffoldResult.ExitCode == 0, $"CLI scaffold failed: {scaffoldResult.StandardError}");

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
        Assert.True(validateResult.ExitCode == 0, $"config validate failed: {validateResult.StandardError}");
        Assert.Contains($"Configuration valid for service '{serviceName}'", validateResult.StandardOutput, StringComparison.Ordinal);

        var readyFile = tempDir.Resolve("ready.txt");
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
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);

        await serveProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
        var serveResult = await serveProcess.GetResultAsync(TestContext.Current.CancellationToken);
        Assert.True(serveResult.ExitCode == 0, $"serve command failed: {serveResult.StandardError}");
    }

    [Fact(Timeout = 240_000)]
    public async Task CliCommands_CanIntrospectRequestAndBenchmark_HttpHost()
    {
        var serviceName = $"cli-introspect-{Guid.NewGuid():N}";
        var httpPort = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{httpPort}/");

        var options = new DispatcherOptions(serviceName);
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
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
        await dispatcher.StartOrThrowAsync(ct);

        try
        {
            var introspectResult = await OmniRelayCliTestHelper.RunAsync(
                ["introspect", "--url", $"{baseAddress}omnirelay/introspect", "--format", "json"],
                ct);
            Assert.True(introspectResult.ExitCode == 0, $"introspect failed: {introspectResult.StandardError}");
            using (var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(introspectResult.StandardOutput)
                ? "{}"
                : introspectResult.StandardOutput))
            {
                Assert.True(document.RootElement.TryGetProperty("service", out var serviceProperty));
                Assert.Equal(serviceName, serviceProperty.GetString());
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
            Assert.True(requestResult.ExitCode == 0, $"request failed: {requestResult.StandardError}");
            Assert.Contains("Request succeeded.", requestResult.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("pong:hello-cli", requestResult.StandardOutput, StringComparison.Ordinal);

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
            Assert.True(benchmarkResult.ExitCode == 0, $"benchmark failed: {benchmarkResult.StandardError}");
            Assert.Contains("Benchmark complete.", benchmarkResult.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("Success: 3", benchmarkResult.StandardOutput, StringComparison.Ordinal);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    [Http3Fact(Timeout = 150_000)]
    public async Task CliHttp3Flags_EnableQuicListeners()
    {
        if (!QuicListener.IsSupported)
        {
            return;
        }

        using var tempDir = new TempDirectory();
        var configPath = tempDir.Resolve("appsettings.http3.json");
        var serviceName = $"cli-http3-{Guid.NewGuid():N}";
        using var certificate = TestCertificateFactory.CreateLoopbackCertificate("CN=cli-http3");
        var certificatePath = tempDir.Resolve("server-http3.pfx");
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
        Assert.True(scaffoldResult.ExitCode == 0, $"CLI scaffold failed: {scaffoldResult.StandardError}");

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

        var readyFile = tempDir.Resolve("ready-http3.txt");
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
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(3, response.Version.Major);
        }

        await serveProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
        var serveResult = await serveProcess.GetResultAsync(TestContext.Current.CancellationToken);
        Assert.True(serveResult.ExitCode == 0, $"serve command failed: {serveResult.StandardError}");
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
