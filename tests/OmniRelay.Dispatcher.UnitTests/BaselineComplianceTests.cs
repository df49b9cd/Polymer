using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Core;
using OmniRelay.Dispatcher.Config;
using OmniRelay.TestSupport.Assertions;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public class BaselineComplianceTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void CapabilityManifestExample_Aligns_With_RuntimeCapabilities()
    {
        var manifestPath = Path.Combine(RepositoryRoot.Path, "docs", "capabilities", "manifest-example.json");
        File.Exists(manifestPath).ShouldBeTrue($"Missing manifest at {manifestPath}");

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifestCaps = doc.RootElement.GetProperty("capabilities")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        manifestCaps.ShouldNotBeEmpty();

        var dispatcher = DispatcherConfigMapper.CreateDispatcher(
            new ServiceCollection().BuildServiceProvider(),
            new DispatcherComponentRegistry(),
            new DispatcherConfig
            {
                Service = "svc",
                Mode = "InProc",
                Inbounds = new InboundsConfig
                {
                    Http = { new HttpInboundConfig { Urls = ["http://127.0.0.1:5001"] } }
                },
                Outbounds = new OutboundsConfig(),
                Middleware = new MiddlewareConfig(),
                Encodings = new EncodingConfig()
            },
            configureOptions: null);

        dispatcher.IsSuccess.ShouldBeTrue(dispatcher.Error?.Message);
        var capabilities = dispatcher.Value.Capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);

        capabilities.ShouldContain("feature:aot-safe");
        capabilities.ShouldContain("feature:http3:conditional");

        manifestCaps.ShouldAllBe(capabilities.Contains);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void BannedApiList_Includes_Core_ReflectionBlocks()
    {
        var bannedPath = Path.Combine(RepositoryRoot.Path, "eng", "banned-apis.txt");
        File.Exists(bannedPath).ShouldBeTrue($"Missing banned API list at {bannedPath}");

        var lines = File.ReadAllLines(bannedPath)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
            .ToArray();

        lines.ShouldContain(l => l.Contains("Reflection") && l.Contains("Emit"));
        lines.ShouldContain(l => l.Contains("Type") && l.Contains("GetType"));
        lines.ShouldContain(l => l.Contains("Activator") && l.Contains("CreateInstance"));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void CiGateScript_Enforces_Aot_Publish_For_Core_Hosts()
    {
        var scriptPath = Path.Combine(RepositoryRoot.Path, "eng", "run-ci-gate.sh");
        File.Exists(scriptPath).ShouldBeTrue($"Missing CI gate script at {scriptPath}");

        var script = File.ReadAllText(scriptPath);

        script.ShouldContain("OmniRelay.DataPlane");
        script.ShouldContain("OmniRelay.ControlPlane");
        script.ShouldContain("OmniRelay.Cli");
        script.ShouldContain("PublishAot=true");
    }
}
