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

        var config = $$"""
        {
          "omnirelay": {
            "service": "feature-serve",
            "inbounds": {
              "http": [ { "urls": [ "http://127.0.0.1:{httpPort}/" ] } ],
              "grpc": [ { "urls": [ "http://127.0.0.1:{grpcPort}" ] } ]
            }
          }
        }
        """;
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
        var url = $"http://127.0.0.1:{_application.HttpInboundPort}/omnirelay/introspect";
        var result = await CliCommandRunner.RunAsync(
            $"introspect --url {url} --format json",
            TestContext.Current.CancellationToken);

        result.ExitCode.ShouldBe(0, result.Stdout + result.Stderr);
        result.Stdout.ShouldContain("feature-tests-relay", Case.Insensitive);
    }
}
