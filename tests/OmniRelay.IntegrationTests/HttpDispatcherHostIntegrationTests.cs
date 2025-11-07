using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OmniRelay.Configuration;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.Tests;
using OmniRelay.Transport.Http;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.IntegrationTests;

public class HttpDispatcherHostIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async Task HttpInbound_ConfiguredViaHost_RoundtripsUnaryRequest()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "integration-http",
            ["omnirelay:inbounds:http:0:urls:0"] = baseUrl
        });

        builder.Services.AddLogging();
        builder.Services.AddOmniRelayDispatcher(builder.Configuration.GetSection("omnirelay"));

        using var host = builder.Build();
        var dispatcher = host.Services.GetRequiredService<Dispatcher.Dispatcher>();

        dispatcher.Register(new UnaryProcedureSpec(
            "integration-http",
            "integration::ping",
            static (request, _) =>
            {
                var responseBytes = Encoding.UTF8.GetBytes("integration-http-response");
                var responseMeta = new ResponseMeta(encoding: MediaTypeNames.Text.Plain);
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(responseBytes, responseMeta)));
            }));

        var ct = TestContext.Current.CancellationToken;
        await host.StartAsync(ct);

        try
        {
            using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/");
            httpRequest.Headers.Add(HttpTransportHeaders.Procedure, "integration::ping");
            httpRequest.Headers.Add(HttpTransportHeaders.Transport, "http");
            httpRequest.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("ping"));

            using var response = await httpClient.SendAsync(httpRequest, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(ct);
            Assert.Equal("integration-http-response", body);

            Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Encoding, out var encodingValues));
            Assert.Contains(MediaTypeNames.Text.Plain, encodingValues);
            Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Transport, out var transportValues));
            Assert.Contains("http", transportValues);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }
}
