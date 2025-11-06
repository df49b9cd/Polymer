using System.Net.Sockets;
using System.Text.Json;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Http;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Tests.Transport;

public sealed class HttpIntrospectionTests
{
    [Fact(Timeout = 30_000)]
    public async Task IntrospectionEndpoint_ReportsDispatcherState()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("inspect");
        var inbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http-inbound", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.Register(new UnaryProcedureSpec(
            "inspect",
            "procedures::ping",
            (request, cancellationToken) =>
            {
                var response = Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta(encoding: "application/json"));
                return ValueTask.FromResult(Ok(response));
            },
            encoding: "application/json"));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);
        await WaitForHttpReadyAsync(baseAddress, ct);

        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };
            using var response = await client.GetAsync("omnirelay/introspect", ct);
            Assert.True(response.IsSuccessStatusCode, $"HTTP {response.StatusCode}");

            await using var responseStream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct);

            var root = document.RootElement;
            Assert.Equal("inspect", root.GetProperty("service").GetString());
            Assert.Equal("Running", root.GetProperty("status").GetString());

            var procedures = root.GetProperty("procedures");
            var unaryProcedures = procedures.GetProperty("unary").EnumerateArray().ToArray();
            var procedure = Assert.Single(unaryProcedures, element => element.GetProperty("name").GetString() == "procedures::ping");
            Assert.Equal("application/json", procedure.GetProperty("encoding").GetString());
            Assert.Equal(0, procedures.GetProperty("oneway").GetArrayLength());
            Assert.Equal(0, procedures.GetProperty("stream").GetArrayLength());
            Assert.Equal(0, procedures.GetProperty("clientStream").GetArrayLength());
            Assert.Equal(0, procedures.GetProperty("duplex").GetArrayLength());

            var components = root.GetProperty("components").EnumerateArray().ToArray();
            Assert.Contains(components, component =>
                string.Equals(component.GetProperty("name").GetString(), "http-inbound", StringComparison.OrdinalIgnoreCase) &&
                component.GetProperty("componentType").GetString()!.Contains(nameof(HttpInbound), StringComparison.Ordinal));

            var middleware = root.GetProperty("middleware");
            Assert.True(middleware.TryGetProperty("inboundUnary", out var inboundUnary));
            Assert.Equal(0, inboundUnary.GetArrayLength());

            Assert.True(middleware.TryGetProperty("outboundUnary", out var outboundUnary));
            Assert.Equal(0, outboundUnary.GetArrayLength());
        }
        finally
        {
            await dispatcher.StopAsync(ct);
        }
    }

    private static async Task WaitForHttpReadyAsync(Uri address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        const int maxAttempts = 100;
        const int connectTimeoutMilliseconds = 200;
        const int settleDelayMilliseconds = 20;
        const int retryDelayMilliseconds = 25;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address.Host, address.Port)
                            .WaitAsync(TimeSpan.FromMilliseconds(connectTimeoutMilliseconds), cancellationToken);

                await Task.Delay(TimeSpan.FromMilliseconds(settleDelayMilliseconds), cancellationToken);
                return;
            }
            catch (SocketException)
            {
                // Listener not ready yet; retry.
            }
            catch (TimeoutException)
            {
                // Connection attempt timed out; retry.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(retryDelayMilliseconds), cancellationToken);
        }

        throw new TimeoutException("The HTTP inbound failed to bind within the allotted time.");
    }
}
