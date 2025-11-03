using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Polymer.Core;
using Polymer.Dispatcher;
using Polymer.Errors;
using Polymer.Transport.Http;
using Xunit;
using static Hugo.Go;

namespace Polymer.Tests.Transport;

public class HttpInboundLifecycleTests
{
    [Fact(Timeout = 30_000)]
    public async Task StopAsync_WaitsForActiveRequestsAndRejectsNewOnes()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("lifecycle");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http-inbound", httpInbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);

        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.Register(new UnaryProcedureSpec(
            "lifecycle",
            "test::slow",
            async (request, token) =>
            {
                requestStarted.TrySetResult();
                await releaseRequest.Task.WaitAsync(token);
                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta()));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };

        using var initialRequest = CreateRpcRequest("test::slow");
        var inFlightTask = httpClient.SendAsync(initialRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        await requestStarted.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopAsync(ct);

        await Task.Delay(100, ct);

        using var rejectedRequest = CreateRpcRequest("test::slow");
        using var rejectedResponse = await httpClient.SendAsync(rejectedRequest, ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejectedResponse.StatusCode);
        Assert.True(rejectedResponse.Headers.TryGetValues("Retry-After", out var retryAfterValues));
        Assert.Contains("1", retryAfterValues);
        Assert.False(stopTask.IsCompleted);

        releaseRequest.TrySetResult();

        using var response = await inFlightTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await stopTask;
    }

    [Fact(Timeout = 30_000)]
    public async Task StopAsync_WithCancellation_CompletesWithoutWaiting()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("lifecycle");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http-inbound", httpInbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);

        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.Register(new UnaryProcedureSpec(
            "lifecycle",
            "test::slow",
            async (request, token) =>
            {
                requestStarted.TrySetResult();
                await releaseRequest.Task.WaitAsync(token);
                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta()));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };

        using var initialRequest = CreateRpcRequest("test::slow");
        var inFlightTask = httpClient.SendAsync(initialRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        await requestStarted.Task.WaitAsync(ct);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopTask = dispatcher.StopAsync(stopCts.Token);

        await stopTask;
        releaseRequest.TrySetResult();

        await Assert.ThrowsAnyAsync<Exception>(async () => await inFlightTask);
    }

    [Fact(Timeout = 30_000)]
    public async Task HealthEndpoints_ReflectDispatcherState()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("health");
        var httpInbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("http-inbound", httpInbound);

        var dispatcher = new Polymer.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "health",
            "ping",
            (request, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta())))));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        using var httpClient = new HttpClient { BaseAddress = baseAddress };

        using var healthResponse = await httpClient.GetAsync("/healthz", ct);
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);

        using var readinessResponse = await httpClient.GetAsync("/readyz", ct);
        Assert.Equal(HttpStatusCode.OK, readinessResponse.StatusCode);

        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Register(new UnaryProcedureSpec(
            "health",
            "slow",
            async (request, token) =>
            {
                slowStarted.TrySetResult();
                await release.Task.WaitAsync(token);
                return Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty, new ResponseMeta()));
            }));

        using var slowRequest = CreateRpcRequest("slow");
        var slowTask = httpClient.SendAsync(slowRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        await slowStarted.Task.WaitAsync(ct);

        var stopTask = dispatcher.StopAsync(ct);

        using var drainingReadiness = await httpClient.GetAsync("/readyz", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, drainingReadiness.StatusCode);

        release.TrySetResult();
        using var slowResponse = await slowTask;
        Assert.Equal(HttpStatusCode.OK, slowResponse.StatusCode);

        await stopTask;
    }

    private static HttpRequestMessage CreateRpcRequest(string procedure)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/");
        request.Headers.Add(HttpTransportHeaders.Procedure, procedure);
        request.Headers.Add(HttpTransportHeaders.Transport, "http");
        request.Content = new ByteArrayContent([]);
        return request;
    }
}
