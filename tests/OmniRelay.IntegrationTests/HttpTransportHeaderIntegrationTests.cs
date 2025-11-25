using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Transport.Http;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.IntegrationTests;

public class HttpTransportHeaderIntegrationTests
{
    private const string ProtobufContentType = "application/x-protobuf";
    private const string ProtobufNormalizedEncoding = "protobuf";

    [Fact(Timeout = 30_000)]
    public async ValueTask UnaryRequests_SurfaceRpcHeadersForJsonAndProtobuf()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var jsonMetaSource = new TaskCompletionSource<RequestMeta>(TaskCreationOptions.RunContinuationsAsynchronously);
        var protoMetaSource = new TaskCompletionSource<RequestMeta>(TaskCreationOptions.RunContinuationsAsynchronously);

        var options = new DispatcherOptions("headers-service");
        var inbound = HttpInbound.TryCreate([baseAddress.ToString()]).ValueOrChecked();
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
        await dispatcher.StartAsyncChecked(ct);

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
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypeNames.Application.Json);
                response.Headers.TryGetValues(HttpTransportHeaders.Transport, out var transportValues).Should().BeTrue();
                transportValues.Should().Contain("http");
                response.Headers.TryGetValues(HttpTransportHeaders.Protocol, out var protocolValues).Should().BeTrue();
                protocolValues.Should().Contain("HTTP/1.1");
                response.Headers.TryGetValues(HttpTransportHeaders.Encoding, out var responseEncoding).Should().BeTrue();
                responseEncoding.Should().Contain(MediaTypeNames.Application.Json);

                var body = await response.Content.ReadAsStringAsync(ct);
                body.Should().Be("{\"result\":\"json\"}");
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
            jsonMeta.Service.Should().Be("headers-service");
            jsonMeta.Procedure.Should().Be("headers::json");
            jsonMeta.Caller.Should().Be("integration-client");
            jsonMeta.Encoding.Should().Be(MediaTypeNames.Application.Json);
            jsonMeta.Transport.Should().Be("http");
            jsonMeta.ShardKey.Should().Be("shard-a");
            jsonMeta.RoutingKey.Should().Be("route-a");
            jsonMeta.RoutingDelegate.Should().Be("delegate-a");
            jsonMeta.TimeToLive.Should().Be(TimeSpan.FromMilliseconds(250));
            jsonMeta.Deadline?.ToUniversalTime().ToString("O").Should().Be(deadline);
            jsonMeta.Headers.TryGetValue(HttpTransportHeaders.Protocol, out var jsonProtocol).Should().BeTrue();
            jsonProtocol.Should().Be("HTTP/1.1");

            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "headers::protobuf");
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Encoding, ProtobufContentType);

            using (var requestContent = new ByteArrayContent([0x0A, 0x0B, 0x0C]))
            {
                requestContent.Headers.ContentType = new MediaTypeHeaderValue(ProtobufContentType);

                using var response = await client.PostAsync("/", requestContent, ct);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                response.Headers.TryGetValues(HttpTransportHeaders.Transport, out var transportValues).Should().BeTrue();
                transportValues.Should().Contain("http");
                response.Headers.TryGetValues(HttpTransportHeaders.Encoding, out var responseEncoding).Should().BeTrue();
                responseEncoding.Should().Contain(ProtobufNormalizedEncoding);
                response.Content.Headers.ContentType?.MediaType.Should().Be(ProtobufContentType);
                var body = await response.Content.ReadAsByteArrayAsync(ct);
                body.Should().Equal([0x0A, 0x0B, 0x0C]);
            }

            client.DefaultRequestHeaders.Remove(HttpTransportHeaders.Procedure);
            client.DefaultRequestHeaders.Remove(HttpTransportHeaders.Encoding);

            var protoMeta = await protoMetaSource.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            protoMeta.Encoding.Should().Be(ProtobufContentType);
            protoMeta.Transport.Should().Be("http");
            protoMeta.Headers.TryGetValue(HttpTransportHeaders.Protocol, out var protoProtocol).Should().BeTrue();
            protoProtocol.Should().Be("HTTP/1.1");
        }
        finally
        {
            await dispatcher.StopAsyncChecked(CancellationToken.None);
        }
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask UnaryFailures_WriteRpcStatusHeaders()
    {
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var options = new DispatcherOptions("headers-errors");
        var inbound = HttpInbound.TryCreate([baseAddress.ToString()]).ValueOrChecked();
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
        await dispatcher.StartAsyncChecked(ct);

        try
        {
            using var client = new HttpClient { BaseAddress = baseAddress };
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Procedure, "headers::fail");
            client.DefaultRequestHeaders.Add(HttpTransportHeaders.Encoding, MediaTypeNames.Application.Json);

            using var requestContent = new StringContent("{}", Encoding.UTF8, MediaTypeNames.Application.Json);
            using var response = await client.PostAsync("/", requestContent, ct);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            response.Headers.TryGetValues(HttpTransportHeaders.Transport, out var transportValues).Should().BeTrue();
            transportValues.Should().Contain("http");
            response.Headers.TryGetValues(HttpTransportHeaders.Protocol, out var protocolValues).Should().BeTrue();
            protocolValues.Should().Contain("HTTP/1.1");
            response.Headers.TryGetValues(HttpTransportHeaders.Status, out var statusValues).Should().BeTrue();
            statusValues.Should().Contain(nameof(OmniRelayStatusCode.InvalidArgument));
            response.Headers.TryGetValues(HttpTransportHeaders.ErrorCode, out var codeValues).Should().BeTrue();
            codeValues.Should().Contain("invalid-argument");
            response.Headers.TryGetValues(HttpTransportHeaders.ErrorMessage, out var messageValues).Should().BeTrue();
            messageValues.Should().Contain("invalid payload");
            response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypeNames.Application.Json);

            var body = await response.Content.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(body);
            json.RootElement.GetProperty("message").GetString().Should().Be("invalid payload");
            json.RootElement.GetProperty("status").GetString().Should().Be("INVALID_ARGUMENT");
            json.RootElement.GetProperty("code").GetString().Should().Be("invalid-argument");
        }
        finally
        {
            await dispatcher.StopAsyncChecked(CancellationToken.None);
        }
    }
}
