using System.Runtime.CompilerServices;

namespace OmniRelay.Tests.Support;

internal static class TestRuntimeConfigurator
{
    [ModuleInitializer]
    public static void EnableHttp3Support()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http3Support", true);
        AppContext.SetSwitch("System.Net.SocketsHttpHandler.Http3Support", true);
    }
}
