using System.Net;
using System.Net.Mime;
using System.Text;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Transport.Http;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.IntegrationTests;

public class HttpStreamingIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async Task ServerStream_EmitsSseFramesOverHttp()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("stream-service");
        var inbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("stream-http", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new StreamProcedureSpec(
            "stream-service",
            "stream::events",
            (request, callOptions, cancellationToken) =>
            {
                _ = callOptions;
                var call = HttpStreamCall.CreateServerStream(request.Meta, new ResponseMeta(encoding: MediaTypeNames.Text.Plain));

                _ = Task.Run(async () =>
                {
                    try
                    {
                        for (var index = 0; index < 3; index++)
                        {
                            var payload = Encoding.UTF8.GetBytes($"event-{index}");
                            await call.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        await call.CompleteAsync().ConfigureAwait(false);
                    }
                }, cancellationToken);

                return ValueTask.FromResult(Ok<IStreamCall>(call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "stream::events");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

            using var response = await client.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.True(response.Headers.TryGetValues("X-Accel-Buffering", out var bufferingValues));
            Assert.Contains("no", bufferingValues);

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var events = new List<string>();

            while (events.Count < 3)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    break;
                }

                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    events.Add(line["data:".Length..].Trim());
                }
            }

            Assert.Equal(new[] { "event-0", "event-1", "event-2" }, events);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ServerStream_EnforcesMessageSizeLimit()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");
        var runtime = new HttpServerRuntimeOptions { ServerStreamMaxMessageBytes = 8 };
        HttpStreamCall? streamCall = null;

        var options = new DispatcherOptions("stream-limit");
        var inbound = new HttpInbound([baseAddress.ToString()], serverRuntimeOptions: runtime);
        options.AddLifecycle("stream-limit-http", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new StreamProcedureSpec(
            "stream-limit",
            "stream::oversized",
            (request, callOptions, cancellationToken) =>
            {
                _ = callOptions;
                var call = HttpStreamCall.CreateServerStream(request.Meta, new ResponseMeta(encoding: MediaTypeNames.Text.Plain));
                streamCall = call;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var payload = "payload-exceeds-limit"u8.ToArray();
                        await call.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Stream was aborted by the server.
                    }
                }, cancellationToken);

                return ValueTask.FromResult(Ok<IStreamCall>(call));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartOrThrowAsync(ct);

        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "stream::oversized");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");

            using var response = await client.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // Connection was aborted because the payload exceeded the configured limit.
            }

            Assert.NotNull(streamCall);
            await WaitForCompletionAsync(streamCall!, ct);
            Assert.Equal(StreamCompletionStatus.Faulted, streamCall!.Context.CompletionStatus);
            Assert.NotNull(streamCall.Context.CompletionError);
            Assert.Equal(OmniRelayStatusCode.ResourceExhausted, OmniRelayErrorAdapter.ToStatus(streamCall.Context.CompletionError!));
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }

    private static async Task WaitForCompletionAsync(HttpStreamCall call, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (call.Context.CompletionStatus != StreamCompletionStatus.None)
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Server stream did not complete.");
            }

            await Task.Delay(50, cancellationToken);
        }
    }
}
