using System.Net.Quic;

namespace OmniRelay.IntegrationTests.Support;

internal static class Http3TestHelper
{
    private const string FlagName = "OMNIRELAY_ENABLE_HTTP3_TESTS";

    public static bool IsHttp3Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(FlagName), "true", StringComparison.OrdinalIgnoreCase) &&
        QuicListener.IsSupported;
}
