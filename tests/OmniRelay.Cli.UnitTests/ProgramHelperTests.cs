using System.Globalization;
using System.Text;
using OmniRelay.Cli.UnitTests.Infrastructure;

namespace OmniRelay.Cli.UnitTests;

public sealed class ProgramHelperTests : CliTestBase
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void TryBuildConfiguration_ReturnsFalse_WhenFileMissing()
    {
        var success = Program.TryBuildConfiguration(new[] { "missing.json" }, Array.Empty<string>(), out var configuration, out var error);

        success.ShouldBeFalse();
        configuration.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("does not exist");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryBuildConfiguration_LoadsOverrides()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"omnirelay-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, """{"omnirelay":{"service":{"name":"original"}}}""");
        try
        {
            var overrides = new[] { "omnirelay:service:name=overridden" };
            var success = Program.TryBuildConfiguration(new[] { configPath }, overrides, out var configuration, out var error);

            success.ShouldBeTrue();
            error.ShouldBeNull();
            (configuration?["omnirelay:service:name"]).ShouldBe("overridden");
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
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

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryBuildRequestInvocation_ParsesTimeBudgetOptions()
    {
        var deadlineText = "2030-11-01T05:30:00Z";
        var success = Program.TryBuildRequestInvocation(
            transport: "http",
            service: "demo",
            procedure: "Echo/Call",
            caller: "caller",
            encoding: null,
            headerValues: Array.Empty<string>(),
            profileValues: Array.Empty<string>(),
            shardKey: null,
            routingKey: null,
            routingDelegate: null,
            protoFiles: Array.Empty<string>(),
            protoMessage: null,
            ttlOption: "5s",
            deadlineOption: deadlineText,
            timeoutOption: "00:00:10",
            body: "{}",
            bodyFile: null,
            bodyBase64: null,
            httpUrl: "http://localhost:8080",
            addresses: Array.Empty<string>(),
            enableHttp3: false,
            enableGrpcHttp3: false,
            out var invocation,
            out var error);

        success.ShouldBeTrue();
        error.ShouldBeNull();
        invocation.Request.Meta.TimeToLive.ShouldBe(TimeSpan.FromSeconds(5));

        var expectedDeadline = DateTimeOffset.Parse(deadlineText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        invocation.Request.Meta.Deadline.ShouldBe(expectedDeadline);
        invocation.Timeout.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryBuildRequestInvocation_AppliesJsonPrettyProfile()
    {
        var success = Program.TryBuildRequestInvocation(
            transport: "http",
            service: "demo",
            procedure: "Echo/Call",
            caller: null,
            encoding: null,
            headerValues: Array.Empty<string>(),
            profileValues: new[] { "json:pretty" },
            shardKey: null,
            routingKey: null,
            routingDelegate: null,
            protoFiles: Array.Empty<string>(),
            protoMessage: null,
            ttlOption: null,
            deadlineOption: null,
            timeoutOption: null,
            body: "{\"message\":\"hello\"}",
            bodyFile: null,
            bodyBase64: null,
            httpUrl: "http://localhost:8080",
            addresses: Array.Empty<string>(),
            enableHttp3: false,
            enableGrpcHttp3: false,
            out var invocation,
            out var error);

        success.ShouldBeTrue();
        error.ShouldBeNull();

        var payloadText = Encoding.UTF8.GetString(invocation.Request.Body.Span);
        payloadText.ReplaceLineEndings("\n").ShouldBe("{\n  \"message\": \"hello\"\n}");

        invocation.Request.Meta.Encoding.ShouldBe("application/json");
        invocation.Request.Meta.Headers["Content-Type"].ShouldBe("application/json");
        invocation.Request.Meta.Headers["Accept"].ShouldBe("application/json");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryBuildRequestInvocation_ProtobufProfileEncodesPayload()
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
        invocation.Request.Meta.Encoding.ShouldBe("application/x-protobuf");
        invocation.Request.Meta.Headers["Content-Type"].ShouldBe("application/x-protobuf");
        invocation.Request.Meta.Headers["Rpc-Encoding"].ShouldBe("application/x-protobuf");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryBuildRequestInvocation_Http3RequiresHttpsUrl()
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
            bodyBase64: null,
            httpUrl: "http://localhost:8080",
            addresses: Array.Empty<string>(),
            enableHttp3: true,
            enableGrpcHttp3: false,
            out var invocation,
            out var error);

        success.ShouldBeFalse();
        invocation.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("HTTP/3 requires an HTTPS --url.");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryBuildRequestInvocation_Http3ConfiguresRuntimeWhenHttpsProvided()
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
            bodyBase64: null,
            httpUrl: "https://localhost:8443",
            addresses: Array.Empty<string>(),
            enableHttp3: true,
            enableGrpcHttp3: false,
            out var invocation,
            out var error);

        success.ShouldBeTrue();
        error.ShouldBeNull();
        invocation.HttpClientRuntime.ShouldNotBeNull();
        invocation.HttpClientRuntime!.EnableHttp3.ShouldBeTrue();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryBuildRequestInvocation_GrpcHttp3RequiresHttpsAddresses()
    {
        var success = Program.TryBuildRequestInvocation(
            transport: "grpc",
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
            bodyBase64: null,
            httpUrl: null,
            addresses: new[] { "http://localhost:9090" },
            enableHttp3: false,
            enableGrpcHttp3: true,
            out var invocation,
            out var error);

        success.ShouldBeFalse();
        invocation.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("HTTP/3 requires HTTPS gRPC addresses");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryBuildRequestInvocation_GrpcHttp3ConfiguresRuntimeWhenHttpsProvided()
    {
        var success = Program.TryBuildRequestInvocation(
            transport: "grpc",
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
            bodyBase64: null,
            httpUrl: null,
            addresses: new[] { "https://localhost:9091" },
            enableHttp3: false,
            enableGrpcHttp3: true,
            out var invocation,
            out var error);

        success.ShouldBeTrue();
        error.ShouldBeNull();
        invocation.GrpcClientRuntime.ShouldNotBeNull();
        invocation.GrpcClientRuntime!.EnableHttp3.ShouldBeTrue();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryParseDuration_ParsesSuffixValues()
    {
        Program.TryParseDuration("5s", out var seconds).ShouldBeTrue();
        seconds.ShouldBe(TimeSpan.FromSeconds(5));

        Program.TryParseDuration("2m", out var minutes).ShouldBeTrue();
        minutes.ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryParseDuration_InvalidString_ReturnsFalse()
    {
        Program.TryParseDuration("not-a-duration", out _).ShouldBeFalse();
    }

    [Fact(Timeout = TestTimeouts.Default)]
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
