using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Tests.Support;
using OmniRelay.Transport.Http;
using Xunit;
using static OmniRelay.IntegrationTests.Support.TransportTestHelper;

namespace OmniRelay.IntegrationTests.Transport.Http;

public sealed class HttpsBindingTests : TransportIntegrationTest
{
    public HttpsBindingTests(ITestOutputHelper output)
        : base(output)
    {
    }

    [Fact(Timeout = 30000)]
    public async Task Https_WithCertificate_BindsAndServes()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"https://127.0.0.1:{port}/");

        using var cert = TestCertificateFactory.CreateLoopbackCertificate("CN=omnirelay-test");

        var options = new DispatcherOptions("https");
        var tls = new HttpServerTlsOptions { Certificate = cert };
        var inbound = new HttpInbound([baseAddress.ToString()], serverTlsOptions: tls);
        options.AddLifecycle("https-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "https",
            "ping",
            (req, _) => ValueTask.FromResult(Hugo.Go.Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartDispatcherAsync(nameof(Https_WithCertificate_BindsAndServes), dispatcher, ct, ownsLifetime: false);
        await WaitForHttpEndpointReadyAsync(baseAddress, ct);

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
        };
        using var httpClient = new HttpClient(handler) { BaseAddress = baseAddress };
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "ping");
        using var response = await httpClient.PostAsync("/", new ByteArrayContent([]), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stopResult = await dispatcher.StopAsync(ct);
        Assert.True(stopResult.IsSuccess);
    }

    [Fact(Timeout = 30000)]
    public async Task Https_WithoutCertificate_ThrowsOnStart()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"https://127.0.0.1:{port}/");

        var options = new DispatcherOptions("https");
        var inbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("https-inbound", inbound);
        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        var startResult = await dispatcher.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(startResult.IsFailure);
    }

}
