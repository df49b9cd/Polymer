using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.Tests.Transport.Http;

public class MetaHeadersTests
{
    [Fact(Timeout = 30000)]
    public async Task TtlAndDeadlineHeaders_RoundTripIntoRequestMeta()
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
                var json = JsonSerializer.Serialize(new { ttlMs, deadline });
                var bytes = Encoding.UTF8.GetBytes(json);
                return ValueTask.FromResult(Hugo.Go.Ok(Response<ReadOnlyMemory<byte>>.Create(bytes, new ResponseMeta(encoding: "application/json"))));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "meta::echo");
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.TtlMs, "1500");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O");
        httpClient.DefaultRequestHeaders.Add(HttpTransportHeaders.Deadline, deadline);

        using var response = await httpClient.PostAsync("/", new ByteArrayContent([]), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonSerializer.Deserialize<JsonElement>(body);

        Assert.Equal(1500, doc.GetProperty("ttlMs").GetDouble(), precision: 0);
        Assert.Equal(deadline, doc.GetProperty("deadline").GetString());

        await dispatcher.StopOrThrowAsync(ct);
    }
}
