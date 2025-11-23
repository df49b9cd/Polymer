using System.Globalization;
using OmniRelay.Cli.UnitTests.Infrastructure;

namespace OmniRelay.Cli.UnitTests;

public sealed class ProgramPayloadTests : CliTestBase
{
    private const int PayloadLimitBytes = 1_048_576; // mirror Program.MaxPayloadBytes

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryBuildRequestInvocation_RejectsOversizedBase64()
    {
        var oversized = new byte[PayloadLimitBytes + 1];
        var base64 = Convert.ToBase64String(oversized);

        var success = Program.TryBuildRequestInvocation(
            transport: "http",
            service: "oversized",
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
            body: null,
            bodyFile: null,
            bodyBase64: base64,
            httpUrl: "https://localhost:8443",
            addresses: Array.Empty<string>(),
            enableHttp3: true,
            enableGrpcHttp3: false,
            out var invocation,
            out var error);

        success.ShouldBeFalse();
        invocation.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("too large", Case.Insensitive);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryBuildRequestInvocation_InvalidBase64_ReturnsError()
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
            body: null,
            bodyFile: null,
            bodyBase64: "@@not-base64@@",
            httpUrl: "http://localhost:8080",
            addresses: Array.Empty<string>(),
            enableHttp3: false,
            enableGrpcHttp3: false,
            out var invocation,
            out var error);

        success.ShouldBeFalse();
        invocation.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("--body-base64", Case.Insensitive);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryBuildRequestInvocation_InlineBodyTooLarge_IsRejected()
    {
        var inlineBody = new string('x', PayloadLimitBytes + 1);

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
            deadlineOption: DateTimeOffset.UtcNow.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture),
            timeoutOption: "00:00:01",
            body: inlineBody,
            bodyFile: null,
            bodyBase64: null,
            httpUrl: "http://localhost:8080",
            addresses: Array.Empty<string>(),
            enableHttp3: false,
            enableGrpcHttp3: false,
            out var invocation,
            out var error);

        success.ShouldBeFalse();
        invocation.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain("Inline body is too large", Case.Insensitive);
    }
}
