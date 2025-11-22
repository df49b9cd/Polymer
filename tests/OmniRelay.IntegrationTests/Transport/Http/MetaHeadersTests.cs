using System.Text.Json;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.IntegrationTests.Support;
using OmniRelay.Transport.Http;
using Xunit;
using static OmniRelay.IntegrationTests.Support.TransportTestHelper;

namespace OmniRelay.IntegrationTests.Transport.Http;

public sealed class MetaHeadersTests(ITestOutputHelper output) : TransportIntegrationTest(output)
{
    [Fact(Timeout = 30000)]
    public async ValueTask TtlAndDeadlineHeaders_RoundTripIntoRequestMeta()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("meta");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http", httpInbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "meta",
            "meta::echo",
            (request, _) =>
            {
                var ttlMs = request.Meta.TimeToLive?.TotalMilliseconds;
                var deadline = request.Meta.Deadline?.ToUniversalTime().ToString("O");
                var payload = new MetaDiagnosticsPayload(ttlMs, deadline);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
                return ValueTask.FromResult(Hugo.Go.Ok(Response<ReadOnlyMemory<byte>>.Create(bytes, new ResponseMeta(encoding: "application/json"))));
            }));

        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartDispatcherAsync(nameof(TtlAndDeadlineHeaders_RoundTripIntoRequestMeta), dispatcher, ct);
        await WaitForHttpEndpointReadyAsync(baseAddress, ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "meta::echo");
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.TtlMs, "1500");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O");
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Deadline, deadline);

        using var response = await httpClient.PostAsync("/", new ByteArrayContent([]), ct);
        var body = await response.Content.ReadAsByteArrayAsync(ct);
        var payload = JsonSerializer.Deserialize<MetaDiagnosticsPayload>(body);

        Assert.Equal(1500d, payload?.TtlMs ?? double.NaN, precision: 0);
        Assert.Equal(deadline, payload?.Deadline);
    }
}

internal sealed record MetaDiagnosticsPayload(double? TtlMs, string? Deadline);
