using System.Text.Json;
using AwesomeAssertions;
using Xunit;

namespace OmniRelay.IntegrationTests.Cli;

public class ConfigScaffoldTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Scaffold_Includes_Http3_Toggles_When_Requested()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "omnirelay_scaffold_tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        var output = Path.Combine(tempDir, "appsettings.json");

        var exit = await global::OmniRelay.Cli.Program.Main(
            [
            "config", "scaffold",
            "--output", output,
            "--section", "omnirelay",
            "--service", "payments",
            "--http3-http",
            "--http3-grpc",
            "--include-outbound",
            "--outbound-service", "ledger"
        ]);

        exit.Should().Be(0);
        File.Exists(output).Should().BeTrue($"Expected scaffold file at '{output}'.");

        var json = await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.TryGetProperty("omnirelay", out var omnirelay).Should().BeTrue();
        omnirelay.TryGetProperty("inbounds", out var inbounds).Should().BeTrue();

        // HTTP inbound contains an HTTPS entry with enableHttp3
        var http = inbounds.GetProperty("http");
        http.GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
        var httpsEntry = http.EnumerateArray().FirstOrDefault(e => e.TryGetProperty("runtime", out var rt) && rt.TryGetProperty("enableHttp3", out var enabled) && enabled.GetBoolean());
        (httpsEntry.ValueKind != JsonValueKind.Undefined).Should().BeTrue("HTTPS HTTP inbound with enableHttp3 missing.");

        // gRPC inbound contains an HTTPS entry with enableHttp3
        var grpc = inbounds.GetProperty("grpc");
        grpc.GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
        var grpcHttps = grpc.EnumerateArray().FirstOrDefault(e => e.TryGetProperty("runtime", out var rt) && rt.TryGetProperty("enableHttp3", out var enabled) && enabled.GetBoolean());
        (grpcHttps.ValueKind != JsonValueKind.Undefined).Should().BeTrue("HTTPS gRPC inbound with enableHttp3 missing.");

        // Outbound section includes endpoints with supportsHttp3 markers
        omnirelay.TryGetProperty("outbounds", out var outbounds).Should().BeTrue();
        var ledger = outbounds.GetProperty("ledger");
        var unary = ledger.GetProperty("unary");
        var grpcOut = unary.GetProperty("grpc");
        grpcOut.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
        var first = grpcOut[0];
        var endpoints = first.GetProperty("endpoints");
        endpoints.GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
        var anyH3 = endpoints.EnumerateArray().Any(e => e.TryGetProperty("supportsHttp3", out var flag) && flag.GetBoolean());
        anyH3.Should().BeTrue("No outbound endpoint marked supportsHttp3=true.");
    }
}
