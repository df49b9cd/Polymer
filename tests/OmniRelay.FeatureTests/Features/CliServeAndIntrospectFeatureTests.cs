using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using OmniRelay.FeatureTests.Fixtures;
using OmniRelay.Tests;
using Xunit;

namespace OmniRelay.FeatureTests.Features;

[Collection(nameof(FeatureTestCollection))]
public sealed class CliServeAndIntrospectFeatureTests
{
    private readonly FeatureTestApplication _application;

    public CliServeAndIntrospectFeatureTests(FeatureTestApplication application)
    {
        _application = application;
    }

    [Fact(Timeout = 180_000)]
    public async ValueTask ServeCommand_WritesReadyFile_ExitsCleanly()
    {
        var httpPort = TestPortAllocator.GetRandomPort();
        var grpcPort = TestPortAllocator.GetRandomPort();
        var configPath = Path.Combine(Path.GetTempPath(), $"feature-serve-{Guid.NewGuid():N}.json");
        var readyFile = Path.Combine(Path.GetTempPath(), $"feature-serve-ready-{Guid.NewGuid():N}.txt");

        var config = string.Format(CultureInfo.InvariantCulture,
@"{{
  ""omnirelay"": {{
    ""service"": ""feature-serve"",
    ""inbounds"": {{
      ""http"": [ {{ ""urls"": [ ""http://127.0.0.1:{0}/"" ] }} ],
      ""grpc"": [ {{ ""urls"": [ ""http://127.0.0.1:{1}"" ] }} ]
    }}
  }}
}}", httpPort, grpcPort);
        await File.WriteAllTextAsync(configPath, config, TestContext.Current.CancellationToken);

        try
        {
            var command = $"serve --config {configPath} --section omnirelay --ready-file {readyFile} --shutdown-after 00:00:01";
            var result = await CliCommandRunner.RunAsync(command, TestContext.Current.CancellationToken);

            result.ExitCode.ShouldBe(0, result.Stdout + result.Stderr);
            File.Exists(readyFile).ShouldBeTrue("ready file not written");
        }
        finally
        {
            if (File.Exists(configPath))
            {
                File.Delete(configPath);
            }

            if (File.Exists(readyFile))
            {
                File.Delete(readyFile);
            }
        }
    }

    [Fact(Timeout = 120_000)]
    public async ValueTask IntrospectCommand_HitsRunningDispatcher()
    {
        var port = TestPortAllocator.GetRandomPort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, port));
        var app = builder.Build();

        const string payload = """
        {
          "service": "feature-tests-relay",
          "status": "Running",
          "procedures": {
            "unary": [],
            "oneway": [],
            "stream": [],
            "clientStream": [],
            "duplex": []
          },
          "components": [],
          "outbounds": [],
          "middleware": {
            "inboundUnary": [],
            "inboundOneway": [],
            "inboundStream": [],
            "inboundClientStream": [],
            "inboundDuplex": [],
            "outboundUnary": [],
            "outboundOneway": [],
            "outboundStream": [],
            "outboundClientStream": [],
            "outboundDuplex": []
          }
        }
        """;

        app.MapGet("/omnirelay/introspect", () => Results.Content(payload, "application/json"));

        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var url = $"http://127.0.0.1:{port}/omnirelay/introspect";
            var result = await CliCommandRunner.RunAsync(
                $"introspect --url {url} --format json",
                TestContext.Current.CancellationToken);

            result.ExitCode.ShouldBe(0, result.Stdout + result.Stderr);
            result.Stdout.ShouldContain("feature-tests-relay", Case.Insensitive);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}
