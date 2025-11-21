using System.Net.Mime;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Http;
using OmniRelay.FeatureTests.Fixtures;
using OmniRelay.Tests;
using Xunit;

namespace OmniRelay.FeatureTests.Features;

public sealed class CliRequestAndBenchmarkFeatureTests : IAsyncLifetime
{
    private Dispatcher.Dispatcher? _dispatcher;
    private Uri? _baseAddress;
    private string _serviceName = string.Empty;

    public async ValueTask InitializeAsync()
    {
        _serviceName = $"feature-cli-{Guid.NewGuid():N}";
        var port = TestPortAllocator.GetRandomPort();
        _baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions(_serviceName);
        var httpInbound = new HttpInbound([_baseAddress.ToString()]);
        options.AddLifecycle("feature-http", httpInbound);

        _dispatcher = new Dispatcher.Dispatcher(options);
        _dispatcher.Register(new UnaryProcedureSpec(
            _serviceName,
            "cli::ping",
            static (request, _) =>
            {
                var input = System.Text.Encoding.UTF8.GetString(request.Body.Span);
                var payload = System.Text.Encoding.UTF8.GetBytes($"pong:{input}");
                var meta = new ResponseMeta(encoding: MediaTypeNames.Text.Plain, transport: "http");
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(payload, meta)));
            }));

        await _dispatcher.StartOrThrowAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_dispatcher is not null)
        {
            await _dispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    [Fact(Timeout = 120_000)]
    public async ValueTask RequestAndBenchmark_AgainstDispatcher_Succeed()
    {
        var requestResult = await CliCommandRunner.RunAsync(
            $"request --transport http --url {_baseAddress} --service {_serviceName} --procedure cli::ping --encoding {MediaTypeNames.Text.Plain} --body hello-feature",
            TestContext.Current.CancellationToken);

        requestResult.ExitCode.ShouldBe(0, requestResult.Stdout + requestResult.Stderr);
        requestResult.Stdout.ShouldContain("Request succeeded", Case.Insensitive);
        requestResult.Stdout.ShouldContain("pong:hello-feature", Case.Insensitive);

        var benchmarkResult = await CliCommandRunner.RunAsync(
            $"benchmark --transport http --url {_baseAddress} --service {_serviceName} --procedure cli::ping --encoding {MediaTypeNames.Text.Plain} --body load-test --concurrency 1 --requests 2",
            TestContext.Current.CancellationToken);

        benchmarkResult.ExitCode.ShouldBe(0, benchmarkResult.Stdout + benchmarkResult.Stderr);
        benchmarkResult.Stdout.ShouldContain("Benchmark complete", Case.Insensitive);
        benchmarkResult.Stdout.ShouldContain("Success: 2", Case.Insensitive);
    }
}
