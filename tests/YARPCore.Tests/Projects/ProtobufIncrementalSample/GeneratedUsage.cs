using YARPCore.Tests.Protos;
using YARPCore.Dispatcher;

namespace ProtobufIncrementalSample;

public static class GeneratedUsage
{
    public static void Use(Dispatcher dispatcher)
    {
        var client = TestServiceYARPCore.CreateTestServiceClient(dispatcher, "yarpcore.tests.codegen");
        _ = client;
    }
}
