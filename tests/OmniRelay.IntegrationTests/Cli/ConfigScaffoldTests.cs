using System.Text.Json;
using Xunit;

namespace OmniRelay.IntegrationTests.Cli;

public class ConfigScaffoldTests
{
    [Fact]
    public async Task Scaffold_Includes_Http3_Toggles_When_Requested()
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

        Assert.Equal(0, exit);
        Assert.True(File.Exists(output), $"Expected scaffold file at '{output}'.");

        var json = await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.True(root.TryGetProperty("omnirelay", out var omnirelay));
        Assert.True(omnirelay.TryGetProperty("inbounds", out var inbounds));

        // HTTP inbound contains an HTTPS entry with enableHttp3
        var http = inbounds.GetProperty("http");
        Assert.True(http.GetArrayLength() >= 2);
        var httpsEntry = http.EnumerateArray().FirstOrDefault(e => e.TryGetProperty("runtime", out var rt) && rt.TryGetProperty("enableHttp3", out var enabled) && enabled.GetBoolean());
        Assert.True(httpsEntry.ValueKind != JsonValueKind.Undefined, "HTTPS HTTP inbound with enableHttp3 missing.");

        // gRPC inbound contains an HTTPS entry with enableHttp3
        var grpc = inbounds.GetProperty("grpc");
        Assert.True(grpc.GetArrayLength() >= 2);
        var grpcHttps = grpc.EnumerateArray().FirstOrDefault(e => e.TryGetProperty("runtime", out var rt) && rt.TryGetProperty("enableHttp3", out var enabled) && enabled.GetBoolean());
        Assert.True(grpcHttps.ValueKind != JsonValueKind.Undefined, "HTTPS gRPC inbound with enableHttp3 missing.");

        // Outbound section includes endpoints with supportsHttp3 markers
        Assert.True(omnirelay.TryGetProperty("outbounds", out var outbounds));
        var ledger = outbounds.GetProperty("ledger");
        var unary = ledger.GetProperty("unary");
        var grpcOut = unary.GetProperty("grpc");
        Assert.True(grpcOut.GetArrayLength() >= 1);
        var first = grpcOut[0];
        var endpoints = first.GetProperty("endpoints");
        Assert.True(endpoints.GetArrayLength() >= 2);
        var anyH3 = endpoints.EnumerateArray().Any(e => e.TryGetProperty("supportsHttp3", out var flag) && flag.GetBoolean());
        Assert.True(anyH3, "No outbound endpoint marked supportsHttp3=true.");
    }
}
