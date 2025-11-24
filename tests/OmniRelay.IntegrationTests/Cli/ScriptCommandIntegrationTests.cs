using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using OmniRelay.Dispatcher;
using OmniRelay.IntegrationTests.Support;
using Xunit;

namespace OmniRelay.IntegrationTests.Cli;

public class ScriptCommandIntegrationTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ScriptRun_ExecutesIntrospectStepAgainstLiveServer()
    {
        var port = TestPortAllocator.GetRandomPort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        var app = builder.Build();

        var snapshot = new DispatcherIntrospection(
            Service: "script-int",
            Status: DispatcherStatus.Running,
            Procedures: new ProcedureGroups([], [], [], [], []),
            Components: [],
            Outbounds: [],
            Middleware: new MiddlewareSummary([], [], [], [], [], [], [], [], [], []));

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };

        app.MapGet("/omnirelay/introspect", () => Results.Json(snapshot, jsonOptions));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var scriptPath = Path.Combine(Path.GetTempPath(), $"script-int-{Guid.NewGuid():N}.json");
        var script = $$"""
        {
          "steps": [
            { "type": "introspect", "url": "http://127.0.0.1:{{port}}/omnirelay/introspect", "format": "text" },
            { "type": "delay", "duration": "00:00:00.05" }
          ]
        }
        """;
        await File.WriteAllTextAsync(scriptPath, script, TestContext.Current.CancellationToken);

        try
        {
            var result = await OmniRelayCliTestHelper.RunAsync(
                ["script", "run", "--file", scriptPath],
                TestContext.Current.CancellationToken);

            result.ExitCode.Should().Be(0, $"Exit:{result.ExitCode}\nStdOut:\n{result.StandardOutput}\nStdErr:\n{result.StandardError}");
            result.StandardOutput.Should().ContainEquivalentOf("Loaded script");
            result.StandardOutput.Should().ContainEquivalentOf("script-int");
        }
        finally
        {
            if (File.Exists(scriptPath))
            {
                File.Delete(scriptPath);
            }

            await app.DisposeAsync();
        }
    }
}
