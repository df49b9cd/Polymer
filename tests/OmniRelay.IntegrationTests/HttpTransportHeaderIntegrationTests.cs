using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Tests;
using OmniRelay.Transport.Http;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.IntegrationTests;

public class HttpTransportHeaderIntegrationTests
{
    private const string ProtobufContentType = "application/x-protobuf";
    private const string ProtobufNormalizedEncoding = "protobuf";

    [Fact(Timeout = 30_000)]
    public async Task UnaryRequests_SurfaceRpcHeadersForJsonAndProtobuf()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var jsonMetaSource = new TaskCompletionSource<RequestMeta>(TaskCreationOptions.RunContinuationsAsynchronously);
        var protoMetaSource = new TaskCompletionSource<RequestMeta>(TaskCreationOptions.RunContinuationsAsynchronously);

        var options = new DispatcherOptions("headers-service");
        var inbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("headers-http", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);

        dispatcher.Register(new UnaryProcedureSpec(
            "headers-service",
            "headers::json",
            (request, _) =>
            {
                jsonMetaSource.TrySetResult(request.Meta);
                var payload = Encoding.UTF8.GetBytes("{\"result\":\"json\"}");
                var meta = new ResponseMeta(encoding: MediaTypeNames.Application.Json)
                    .WithHeader("X-Json", "ok");
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(payload, meta)));
            }));

        dispatcher.Register(new UnaryProcedureSpec(
            "headers-service",
            "headers::protobuf",
            (request, _) =>
            {
                protoMetaSource.TrySetResult(request.Meta);
                var payload = request.Body.ToArray();
                var meta = new ResponseMeta(encoding: ProtobufNormalizedEncoding)
                    .WithHeader("X-Proto", "ok");
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(payload, meta)));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };

            var deadline = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O");

            using (var request = new HttpRequestMessage(HttpMethod.Post, "/"))
            {
                request.Headers.Add(HttpTransportHeaders.Procedure, "headers::json");
                request.Headers.Add(HttpTransportHeaders.Caller, "integration-client");
                request.Headers.Add(HttpTransportHeaders.Encoding, MediaTypeNames.Application.Json);
                request.Headers.Add(HttpTransportHeaders.ShardKey, "shard-a");
                request.Headers.Add(HttpTransportHeaders.RoutingKey, "route-a");
                request.Headers.Add(HttpTransportHeaders.RoutingDelegate, "delegate-a");
                request.Headers.Add(HttpTransportHeaders.TtlMs, "250");
                request.Headers.Add(HttpTransportHeaders.Deadline, deadline);
                request.Content = new StringContent("{\"message\":\"json\"}", Encoding.UTF8, MediaTypeNames.Application.Json);

                using var response = await client.SendAsync(request, ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(MediaTypeNames.Application.Json, response.Content.Headers.ContentType?.MediaType);
                Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Transport, out var transportValues));
                Assert.Contains("http", transportValues);
                Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Protocol, out var protocolValues));
                Assert.Contains("HTTP/1.1", protocolValues);
                Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Encoding, out var responseEncoding));
                Assert.Contains(MediaTypeNames.Application.Json, responseEncoding);

                var body = await response.Content.ReadAsStringAsync(ct);
                Assert.Equal("{\"result\":\"json\"}", body);
            }

            var jsonMeta = await jsonMetaSource.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("headers-service", jsonMeta.Service);
            Assert.Equal("headers::json", jsonMeta.Procedure);
            Assert.Equal("integration-client", jsonMeta.Caller);
            Assert.Equal(MediaTypeNames.Application.Json, jsonMeta.Encoding);
            Assert.Equal("http", jsonMeta.Transport);
            Assert.Equal("shard-a", jsonMeta.ShardKey);
            Assert.Equal("route-a", jsonMeta.RoutingKey);
            Assert.Equal("delegate-a", jsonMeta.RoutingDelegate);
            Assert.Equal(TimeSpan.FromMilliseconds(250), jsonMeta.TimeToLive);
            Assert.Equal(deadline, jsonMeta.Deadline?.ToUniversalTime().ToString("O"));
            Assert.True(jsonMeta.Headers.TryGetValue(HttpTransportHeaders.Protocol, out var jsonProtocol));
            Assert.Equal("HTTP/1.1", jsonProtocol);

            using (var request = new HttpRequestMessage(HttpMethod.Post, "/"))
            {
                request.Headers.Add(HttpTransportHeaders.Procedure, "headers::protobuf");
                request.Headers.Add(HttpTransportHeaders.Encoding, ProtobufContentType);
                request.Content = new ByteArrayContent(new byte[] { 0x0A, 0x0B, 0x0C });
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(ProtobufContentType);

                using var response = await client.SendAsync(request, ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Transport, out var transportValues));
                Assert.Contains("http", transportValues);
                Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Encoding, out var responseEncoding));
                Assert.Contains(ProtobufNormalizedEncoding, responseEncoding);
                Assert.Equal(ProtobufContentType, response.Content.Headers.ContentType?.MediaType);
                var body = await response.Content.ReadAsByteArrayAsync(ct);
                Assert.Equal(new byte[] { 0x0A, 0x0B, 0x0C }, body);
            }

            var protoMeta = await protoMetaSource.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(ProtobufContentType, protoMeta.Encoding);
            Assert.Equal("http", protoMeta.Transport);
            Assert.True(protoMeta.Headers.TryGetValue(HttpTransportHeaders.Protocol, out var protoProtocol));
            Assert.Equal("HTTP/1.1", protoProtocol);
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task UnaryFailures_WriteRpcStatusHeaders()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("headers-errors");
        var inbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("headers-errors-http", inbound);

        var dispatcher = new OmniRelay.Dispatcher.Dispatcher(options);
        dispatcher.Register(new UnaryProcedureSpec(
            "headers-errors",
            "headers::fail",
            (request, _) =>
            {
                var error = OmniRelayErrorAdapter.FromStatus(
                    OmniRelayStatusCode.InvalidArgument,
                    "invalid payload",
                    transport: "http");
                return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(error));
            }));

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsync(ct);

        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/");
            request.Headers.Add(HttpTransportHeaders.Procedure, "headers::fail");
            request.Headers.Add(HttpTransportHeaders.Encoding, MediaTypeNames.Application.Json);
            request.Content = new StringContent("{}", Encoding.UTF8, MediaTypeNames.Application.Json);

            using var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Transport, out var transportValues));
            Assert.Contains("http", transportValues);
            Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Protocol, out var protocolValues));
            Assert.Contains("HTTP/1.1", protocolValues);
            Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Status, out var statusValues));
            Assert.Contains(OmniRelayStatusCode.InvalidArgument.ToString(), statusValues);
            Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.ErrorCode, out var codeValues));
            Assert.Contains("invalid-argument", codeValues);
            Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.ErrorMessage, out var messageValues));
            Assert.Contains("invalid payload", messageValues);
            Assert.Equal(MediaTypeNames.Application.Json, response.Content.Headers.ContentType?.MediaType);

            var body = await response.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("invalid payload", json.RootElement.GetProperty("message").GetString());
            Assert.Equal(OmniRelayStatusCode.InvalidArgument.ToString(), json.RootElement.GetProperty("status").GetString());
            Assert.Equal("invalid-argument", json.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            await dispatcher.StopAsync(CancellationToken.None);
        }
    }
}
