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
                var payload = "{\"result\":\"json\"}"u8.ToArray();
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
        await dispatcher.StartOrThrowAsync(ct);

        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };

            var deadline = DateTimeOffset.UtcNow.AddSeconds(5).ToString("O");

            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "headers::json");
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Caller, "integration-client");
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Encoding, MediaTypeNames.Application.Json);
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.ShardKey, "shard-a");
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.RoutingKey, "route-a");
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.RoutingDelegate, "delegate-a");
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.TtlMs, "250");
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Deadline, deadline);

            using (var jsonContent = new StringContent("{\"message\":\"json\"}", Encoding.UTF8, MediaTypeNames.Application.Json))
            {
                using var response = await client.PostAsync("/", jsonContent, ct);
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

            client.DefaultRequestHeaders.Remove(HttpTransportHeaders.Procedure);
            client.DefaultRequestHeaders.Remove(HttpTransportHeaders.Caller);
            client.DefaultRequestHeaders.Remove(HttpTransportHeaders.ShardKey);
            client.DefaultRequestHeaders.Remove(HttpTransportHeaders.RoutingKey);
            client.DefaultRequestHeaders.Remove(HttpTransportHeaders.RoutingDelegate);
            client.DefaultRequestHeaders.Remove(HttpTransportHeaders.TtlMs);
            client.DefaultRequestHeaders.Remove(HttpTransportHeaders.Deadline);
            client.DefaultRequestHeaders.Remove(HttpTransportHeaders.Encoding);

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

            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "headers::protobuf");
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Encoding, ProtobufContentType);

            using (var requestContent = new ByteArrayContent([0x0A, 0x0B, 0x0C]))
            {
                requestContent.Headers.ContentType = new MediaTypeHeaderValue(ProtobufContentType);

                using var response = await client.PostAsync("/", requestContent, ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Transport, out var transportValues));
                Assert.Contains("http", transportValues);
                Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Encoding, out var responseEncoding));
                Assert.Contains(ProtobufNormalizedEncoding, responseEncoding);
                Assert.Equal(ProtobufContentType, response.Content.Headers.ContentType?.MediaType);
                var body = await response.Content.ReadAsByteArrayAsync(ct);
                Assert.Equal(new byte[] { 0x0A, 0x0B, 0x0C }, body);
            }

            client.DefaultRequestHeaders.Remove(HttpTransportHeaders.Procedure);
            client.DefaultRequestHeaders.Remove(HttpTransportHeaders.Encoding);

            var protoMeta = await protoMetaSource.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(ProtobufContentType, protoMeta.Encoding);
            Assert.Equal("http", protoMeta.Transport);
            Assert.True(protoMeta.Headers.TryGetValue(HttpTransportHeaders.Protocol, out var protoProtocol));
            Assert.Equal("HTTP/1.1", protoProtocol);
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(CancellationToken.None);
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
        await dispatcher.StartOrThrowAsync(ct);

        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "headers::fail");
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Encoding, MediaTypeNames.Application.Json);

            using var requestContent = new StringContent("{}", Encoding.UTF8, MediaTypeNames.Application.Json);
            using var response = await client.PostAsync("/", requestContent, ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Transport, out var transportValues));
            Assert.Contains("http", transportValues);
            Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Protocol, out var protocolValues));
            Assert.Contains("HTTP/1.1", protocolValues);
            Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.Status, out var statusValues));
            Assert.Contains(nameof(OmniRelayStatusCode.InvalidArgument), statusValues);
            Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.ErrorCode, out var codeValues));
            Assert.Contains("invalid-argument", codeValues);
            Assert.True(response.Headers.TryGetValues(HttpTransportHeaders.ErrorMessage, out var messageValues));
            Assert.Contains("invalid payload", messageValues);
            Assert.Equal(MediaTypeNames.Application.Json, response.Content.Headers.ContentType?.MediaType);

            var body = await response.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("invalid payload", json.RootElement.GetProperty("message").GetString());
            Assert.Equal("INVALID_ARGUMENT", json.RootElement.GetProperty("status").GetString());
            Assert.Equal("invalid-argument", json.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            await dispatcher.StopOrThrowAsync(CancellationToken.None);
        }
    }
}
