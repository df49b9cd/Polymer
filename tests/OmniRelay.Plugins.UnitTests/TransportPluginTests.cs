using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Plugins.Internal.Transport;
using OmniRelay.Core.Transport;
using Xunit;

namespace OmniRelay.Plugins.UnitTests;

public class TransportPluginTests
{
    [Fact]
    public void RegistersHttpAndGrpcTransportsWhenUrlsProvided()
    {
        var services = new ServiceCollection();

        services.AddInternalTransportPlugins(options =>
        {
            options.HttpUrls.Add("http://localhost:18080");
            options.GrpcUrls.Add("http://localhost:18090");
        });

        var provider = services.BuildServiceProvider();
        var transports = provider.GetServices<ITransport>().ToArray();

        Assert.Equal(2, transports.Length);
        Assert.Contains(transports, t => t.Name == "http");
        Assert.Contains(transports, t => t.Name == "grpc");
    }
}
