using OmniRelay.Dispatcher;
using OmniRelay.Tests.Protos;

namespace ProtobufIncrementalSample;

public static class GeneratedUsage
{
    public static void Use(Dispatcher dispatcher)
    {
        var client = TestServiceOmniRelay.CreateTestServiceClient(dispatcher, "yarpcore.tests.codegen");
        _ = client;
    }
}
