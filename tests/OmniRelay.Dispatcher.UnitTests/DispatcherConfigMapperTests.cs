using Hugo;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Dispatcher.Config;
using OmniRelay.TestSupport.Assertions;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Dispatcher.UnitTests;

public class DispatcherConfigMapperTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void CreateDispatcher_WithInvalidMode_ReturnsFailure()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var registry = new DispatcherComponentRegistry();

        var config = new DispatcherConfig
        {
            Service = "svc",
            Mode = "BogusMode",
            Inbounds = new InboundsConfig(),
            Outbounds = new OutboundsConfig(),
            Middleware = new MiddlewareConfig(),
            Encodings = new EncodingConfig()
        };

        var result = DispatcherConfigMapper.CreateDispatcher(services, registry, config, configureOptions: null);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe("dispatcher.config.mode_invalid");
        result.Error.Metadata["mode"].ShouldBe("BogusMode");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void CreateDispatcher_WithInvalidHttpOutboundUrl_ReturnsFailureWithExceptionMetadata()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var registry = new DispatcherComponentRegistry();

        var outbound = new OutboundTarget { Url = "not-a-uri" };
        var serviceOutbounds = new ServiceOutboundsConfig();
        serviceOutbounds.Http.Unary.Add(outbound);

        var outbounds = new OutboundsConfig { ["svc"] = serviceOutbounds };

        var config = new DispatcherConfig
        {
            Service = "svc",
            Inbounds = new InboundsConfig(),
            Outbounds = outbounds,
            Middleware = new MiddlewareConfig(),
            Encodings = new EncodingConfig()
        };

        var result = DispatcherConfigMapper.CreateDispatcher(services, registry, config, configureOptions: null);

        result.IsFailure.ShouldBeTrue();
        var error = result.Error!;
        var metadataDump = string.Join(", ", error.Metadata.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        error.TryGetMetadata("exceptionType", out string? exceptionType).ShouldBeTrue(metadataDump);
        exceptionType.ShouldBe(typeof(UriFormatException).FullName);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void CreateDispatcher_WithValidInboundsAndOutbounds_Succeeds()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var registry = new DispatcherComponentRegistry();

        var config = new DispatcherConfig
        {
            Service = "svc",
            Inbounds = new InboundsConfig
            {
                Http = { new HttpInboundConfig { Urls = ["http://127.0.0.1:6101"] } },
                Grpc = { new GrpcInboundConfig { Urls = ["http://127.0.0.1:6201"] } }
            },
            Outbounds = new OutboundsConfig
            {
                ["remote-http"] = new ServiceOutboundsConfig
                {
                    Http = { Unary = { new OutboundTarget { Url = "http://127.0.0.1:6301" } } }
                },
                ["remote-grpc"] = new ServiceOutboundsConfig
                {
                    Grpc = { Unary = { new OutboundTarget { Url = "http://127.0.0.1:6401" } } }
                }
            },
            Middleware = new MiddlewareConfig(),
            Encodings = new EncodingConfig()
        };

        var result = DispatcherConfigMapper.CreateDispatcher(services, registry, config, configureOptions: null);

        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        var dispatcher = result.Value;

        dispatcher.ServiceName.ShouldBe("svc");
        dispatcher.ClientConfig("remote-http").IsSuccess.ShouldBeTrue();
        dispatcher.ClientConfig("remote-grpc").IsSuccess.ShouldBeTrue();
    }
}
