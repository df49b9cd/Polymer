using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Transport.Http;
using Xunit;

namespace OmniRelay.Core.UnitTests.Transport;

public sealed class HttpOutboundOnewayTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Oneway_Backpressure_WhenQueueIsFull()
    {
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new BlockingHandler(blocker);
        using var client = new HttpClient(handler);
        var outbound = HttpOutbound.Create(client, new Uri("http://localhost/oneway")).Value;

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        var request = new Request<ReadOnlyMemory<byte>>(meta, "payload"u8.ToArray());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        var calls = Enumerable.Range(0, 80)
            .Select(_ => ((IOnewayOutbound)outbound).CallAsync(request, cts.Token).AsTask())
            .ToArray();

        await Task.WhenAll(calls);

        var failures = calls.Count(t => t.Result.IsFailure);
        failures.ShouldBeGreaterThan(0, because: "Expected backpressure failures when queue is saturated.");

        blocker.TrySetResult();
        await outbound.StopAsync(cts.Token);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Oneway_Succeeds_WhenQueueDrains()
    {
        using var handler = new SimpleHandler(HttpStatusCode.Accepted);
        using var client = new HttpClient(handler);
        var outbound = HttpOutbound.Create(client, new Uri("http://localhost/oneway"), disposeClient: false).Value;

        var meta = new RequestMeta(service: "svc", procedure: "proc", transport: "http");
        var request = new Request<ReadOnlyMemory<byte>>(meta, "ok"u8.ToArray());

        var result = await ((IOnewayOutbound)outbound).CallAsync(request, CancellationToken.None);
        result.IsSuccess.ShouldBeTrue();
        await outbound.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class BlockingHandler(TaskCompletionSource blockSource) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using (cancellationToken.Register(() => blockSource.TrySetCanceled(cancellationToken)))
            {
                await blockSource.Task.ConfigureAwait(false);
            }

            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    private sealed class SimpleHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }
}
