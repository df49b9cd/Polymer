using System.IO;
using System.Linq;
using OmniRelay.Cli.UnitTests.Infrastructure;

namespace OmniRelay.Cli.UnitTests;

public sealed class ProgramHelperTests : CliTestBase
{
    [Fact]
    public void TryBuildConfiguration_ReturnsFalse_WhenFileMissing()
    {
        var success = Program.TryBuildConfiguration(new[] { "missing.json" }, Array.Empty<string>(), out var configuration, out var error);

        success.ShouldBeFalse();
        configuration.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("does not exist");
    }

    [Fact]
    public void TryBuildConfiguration_LoadsOverrides()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"omnirelay-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, """{"polymer":{"service":{"name":"original"}}}""");
        try
        {
            var overrides = new[] { "polymer:service:name=overridden" };
            var success = Program.TryBuildConfiguration(new[] { configPath }, overrides, out var configuration, out var error);

            success.ShouldBeTrue();
            error.ShouldBeNull();
            (configuration?["polymer:service:name"]).ShouldBe("overridden");
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public void TryBuildRequestInvocation_DetectsConflictingPayloadSources()
    {
        var success = Program.TryBuildRequestInvocation(
            transport: "http",
            service: "demo",
            procedure: "Echo/Call",
            caller: null,
            encoding: null,
            headerValues: Array.Empty<string>(),
            profileValues: Array.Empty<string>(),
            shardKey: null,
            routingKey: null,
            routingDelegate: null,
            protoFiles: Array.Empty<string>(),
            protoMessage: null,
            ttlOption: null,
            deadlineOption: null,
            timeoutOption: null,
            body: "{}",
            bodyFile: null,
            bodyBase64: "aGVsbG8=",
            httpUrl: "http://localhost:8080",
            addresses: Array.Empty<string>(),
            enableHttp3: false,
            enableGrpcHttp3: false,
            out var invocation,
            out var error);

        success.ShouldBeFalse();
        invocation.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("Specify only one of --body, --body-file, or --body-base64.");
    }

    [Fact]
    public void TryBuildRequestInvocation_LoadsProtoDescriptor()
    {
        var descriptorPath = GetSupportPath("Descriptors", "echo.pb");
        var success = Program.TryBuildRequestInvocation(
            transport: "http",
            service: "demo",
            procedure: "Echo/Call",
            caller: null,
            encoding: null,
            headerValues: Array.Empty<string>(),
            profileValues: new[] { "protobuf:echo.EchoRequest" },
            shardKey: null,
            routingKey: null,
            routingDelegate: null,
            protoFiles: new[] { descriptorPath },
            protoMessage: null,
            ttlOption: null,
            deadlineOption: null,
            timeoutOption: null,
            body: "{\"message\":\"hello\"}",
            bodyFile: null,
            bodyBase64: null,
            httpUrl: "https://localhost:9090",
            addresses: Array.Empty<string>(),
            enableHttp3: false,
            enableGrpcHttp3: false,
            out var invocation,
            out var error);

        success.ShouldBeTrue();
        error.ShouldBeNull();
        invocation.Request.Body.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void TryParseDuration_ParsesSuffixValues()
    {
        Program.TryParseDuration("5s", out var seconds).ShouldBeTrue();
        seconds.ShouldBe(TimeSpan.FromSeconds(5));

        Program.TryParseDuration("2m", out var minutes).ShouldBeTrue();
        minutes.ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void TryParseDuration_InvalidString_ReturnsFalse()
    {
        Program.TryParseDuration("not-a-duration", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryWriteReadyFile_LogsFailures()
    {
        var fileSystem = new FakeFileSystem
        {
            WriteException = new IOException("boom")
        };
        var console = new FakeCliConsole();
        CliRuntime.FileSystem = fileSystem;
        CliRuntime.Console = console;

        Program.TryWriteReadyFile("/tmp/unwritable.txt");

        console.Errors.ShouldContain(message => message.Contains("Failed to write ready file"));
    }

    private static string GetSupportPath(params string[] segments)
    {
        var basePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../..", ".."));
        var combined = new[] { "tests", "TestSupport" }.Concat(segments).ToArray();
        return Path.Combine(basePath, Path.Combine(combined));
    }
}
