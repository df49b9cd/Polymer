using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Mime;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Dispatcher;
using OmniRelay.Tests.Protos;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;
using OmniRelay.Transport.Http.Middleware;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.IntegrationTests;

public class ObservabilityDiagnosticsIntegrationTests
{
    private const string HttpMeterName = "OmniRelay.Transport.Http";
    private const string GrpcActivitySourceName = "OmniRelay.Transport.Grpc";

    [Fact(Timeout = 60_000)]
    public async ValueTask IntrospectionAndHealthChecks_ReportHealthyAndDegradedStatus()
    {
        var healthyPort = TestPortAllocator.GetRandomPort();
        var healthyBase = new Uri($"http://127.0.0.1:{healthyPort}/");
        var healthyOptions = new DispatcherOptions("observability-healthy");
        var healthyInbound = HttpInbound.TryCreate([healthyBase]).ValueOrChecked();
        healthyOptions.AddLifecycle("observability-healthy-http", healthyInbound);

        var healthyDispatcher = new Dispatcher.Dispatcher(healthyOptions);
        RegisterPingProcedure(healthyDispatcher);

        var ct = TestContext.Current.CancellationToken;
        await healthyDispatcher.StartAsyncChecked(ct);
        await WaitForHttpReadyAsync(healthyBase, ct);

        try
        {
            using var client = new HttpClient { BaseAddress = healthyBase };

            using (var introspection = await ReadJsonDocumentAsync(client, "omnirelay/introspect", ct))
            {
                var root = introspection.RootElement;
                root.GetProperty("service").GetString().Should().Be("observability-healthy");
                root.GetProperty("status").GetString().Should().Be("Running");

                var components = root.GetProperty("components").EnumerateArray().Select(component => component.GetProperty("name").GetString()).ToArray();
                components.Should().Contain("observability-healthy-http");
            }

            using (var healthz = await ReadJsonDocumentAsync(client, "healthz", ct))
            {
                var root = healthz.RootElement;
                root.GetProperty("status").GetString().Should().Be("ok");
                root.GetProperty("mode").GetString().Should().Be("live");
                root.GetProperty("issues").GetArrayLength().Should().Be(0);
                root.GetProperty("draining").GetBoolean().Should().BeFalse();
            }

            using (var readyz = await ReadJsonDocumentAsync(client, "readyz", ct))
            {
                var root = readyz.RootElement;
                root.GetProperty("status").GetString().Should().Be("ok");
                root.GetProperty("mode").GetString().Should().Be("ready");
                root.GetProperty("issues").GetArrayLength().Should().Be(0);
            }
        }
        finally
        {
            await healthyDispatcher.StopAsyncChecked(CancellationToken.None);
        }

        var degradedPort = TestPortAllocator.GetRandomPort();
        var degradedBase = new Uri($"http://127.0.0.1:{degradedPort}/");
        var degradedDispatcher = new Dispatcher.Dispatcher(new DispatcherOptions("observability-degraded"));
        RegisterPingProcedure(degradedDispatcher);

        var degradedInbound = new HttpInbound([degradedBase.ToString()]);
        degradedInbound.Bind(degradedDispatcher);
        await degradedInbound.StartAsync(ct);
        await WaitForHttpReadyAsync(degradedBase, ct);

        try
        {
            using var client = new HttpClient { BaseAddress = degradedBase };

            using (var readyResponse = await client.GetAsync("/readyz", ct))
            {
                var payload = await readyResponse.Content.ReadAsStringAsync(ct);
                using var readyz = JsonDocument.Parse(payload);

                var issues = readyz.RootElement.GetProperty("issues")
                    .EnumerateArray()
                    .Select(issue => issue.GetString())
                    .ToArray();

                readyResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
                readyz.RootElement.GetProperty("status").GetString().Should().Be("unavailable");
                issues.Should().Contain("dispatcher-status:Created");
            }

            using (var healthz = await ReadJsonDocumentAsync(client, "healthz", ct))
            {
                healthz.RootElement.GetProperty("status").GetString().Should().Be("ok");
            }
        }
        finally
        {
            await degradedInbound.StopAsync(CancellationToken.None);
        }
    }

    [Fact(Timeout = 60_000)]
    public async ValueTask StructuredLoggingAndMetrics_IncludeProtocolAndPeerContext()
    {
        var serviceName = "observability-http";
        var port = TestPortAllocator.GetRandomPort();
        var baseAddress = new Uri($"http://127.0.0.1:{port}/");

        var logProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(logProvider);
        });

        var rpcLogging = new RpcLoggingMiddleware(loggerFactory.CreateLogger<RpcLoggingMiddleware>());
        var httpClientLogging = new HttpClientLoggingMiddleware(loggerFactory.CreateLogger<HttpClientLoggingMiddleware>());
        using var metricCollector = new HttpMetricCollector();

        var options = new DispatcherOptions(serviceName);
        var inbound = new HttpInbound([baseAddress.ToString()]);
        options.AddLifecycle("observability-http-inbound", inbound);
        options.UnaryInboundMiddleware.Add(rpcLogging);

        var dispatcher = new Dispatcher.Dispatcher(options);
        dispatcher.RegisterUnary("diagnostics::echo", builder =>
        {
            builder.Handle(static (request, _) =>
            {
                var meta = new ResponseMeta(encoding: request.Meta.Encoding ?? MediaTypeNames.Application.Json);
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(request.Body, meta)));
            });
        });

        var ct = TestContext.Current.CancellationToken;
        await dispatcher.StartAsyncChecked(ct);
        await WaitForHttpReadyAsync(baseAddress, ct);

        try
        {
            using var httpClient = new HttpClient { BaseAddress = baseAddress };
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/");
            httpRequest.Headers.Add(HttpTransportHeaders.Procedure, "diagnostics::echo");
            httpRequest.Headers.Add(HttpTransportHeaders.Caller, "client-edge");
            httpRequest.Headers.Add(HttpTransportHeaders.Encoding, "json");
            httpRequest.Headers.Add("rpc.peer", "client-edge");
            httpRequest.Content = new StringContent("""{"message":"ping"}""", Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(httpRequest, ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var outboundMeta = new RequestMeta(
                service: serviceName,
                procedure: "diagnostics::echo",
                caller: "observability-client",
                encoding: "json",
                transport: "http",
                headers:
                [
                    new KeyValuePair<string, string>("rpc.peer", "backend-primary")
                ]);

            var outboundRequest = new Request<ReadOnlyMemory<byte>>(outboundMeta, """{"message":"fanout"}"""u8.ToArray());

            var outboundMiddleware = (IUnaryOutboundMiddleware)rpcLogging;
            var outboundResult = await outboundMiddleware.InvokeAsync(
                outboundRequest,
                ct,
                static (request, _) =>
                {
                    var meta = new ResponseMeta(encoding: request.Meta.Encoding ?? "json");
                    return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(request.Body, meta)));
                });

            outboundResult.IsSuccess.Should().BeTrue();

            var middlewareContext = new HttpClientMiddlewareContext(
                new HttpRequestMessage(HttpMethod.Post, "https://backend.example/omnirelay"),
                outboundMeta,
                HttpOutboundCallKind.Unary,
                HttpCompletionOption.ResponseContentRead);

            middlewareContext.Request.Content = new ByteArrayContent(outboundRequest.Body.ToArray());

            HttpClientMiddlewareHandler terminal = (_, _) =>
            {
                var fakeResponse = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                };
                return ValueTask.FromResult(fakeResponse);
            };

            using var fakeResponse = await httpClientLogging.InvokeAsync(middlewareContext, terminal, ct);
            fakeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await dispatcher.StopAsyncChecked(CancellationToken.None);
        }

        var inboundLog = logProvider.Entries.FirstOrDefault(entry => entry.Message.StartsWith("rpc inbound unary completed", StringComparison.OrdinalIgnoreCase));
        inboundLog.Should().NotBeNull();
        AssertScopeContains(inboundLog!, "rpc.transport", "http");
        AssertScopeContains(inboundLog!, "rpc.peer", "client-edge");

        var outboundLog = logProvider.Entries.FirstOrDefault(entry => entry.Message.StartsWith("rpc outbound unary completed", StringComparison.OrdinalIgnoreCase));
        outboundLog.Should().NotBeNull();
        AssertScopeContains(outboundLog!, "rpc.peer", "backend-primary");

        logProvider.Entries.Should().Contain(entry =>
            entry.Category == typeof(HttpClientLoggingMiddleware).FullName &&
            entry.Message.Contains("Sending HTTP outbound request", StringComparison.OrdinalIgnoreCase));

        var requestMetrics = metricCollector.Measurements
            .Where(measurement =>
                measurement.Instrument == "omnirelay.http.requests.completed" &&
                HasTag(measurement, "rpc.service", serviceName))
            .ToArray();

        requestMetrics.Should().NotBeEmpty();
        requestMetrics.Should().Contain(measurement => HasTag(measurement, "rpc.protocol", "HTTP/1.1"));
        requestMetrics.Should().Contain(measurement => HasTag(measurement, "network.transport", "tcp"));
    }

    [Fact(Timeout = 90_000)]
    public async ValueTask OpenTelemetrySpans_RecordTransportAttributesForStreaming()
    {
        var activities = new ConcurrentBag<Activity>();
        using var listener = CreateGrpcActivityListener(activities);

        var serviceName = "observability-grpc";
        var port = TestPortAllocator.GetRandomPort();
        var address = new Uri($"http://127.0.0.1:{port}");

        var serverOptions = new DispatcherOptions(serviceName);
        var grpcInbound = GrpcInbound.TryCreate([address]).ValueOrChecked();
        serverOptions.AddLifecycle("observability-grpc-inbound", grpcInbound);
        var serverDispatcher = new Dispatcher.Dispatcher(serverOptions);
        serverDispatcher.RegisterTestService(new StreamingProbeService());

        var outbound = new GrpcOutbound(address, serviceName);
        var clientOptions = new DispatcherOptions("observability-grpc-client");
        clientOptions.AddUnaryOutbound(serviceName, null, outbound);
        clientOptions.AddOnewayOutbound(serviceName, null, outbound);
        clientOptions.AddStreamOutbound(serviceName, null, outbound);
        clientOptions.AddClientStreamOutbound(serviceName, null, outbound);
        clientOptions.AddDuplexOutbound(serviceName, null, outbound);

        var clientDispatcher = new Dispatcher.Dispatcher(clientOptions);
        var client = TestServiceOmniRelay.CreateTestServiceClient(clientDispatcher, serviceName);

        var ct = TestContext.Current.CancellationToken;
        await serverDispatcher.StartAsyncChecked(ct);
        await clientDispatcher.StartAsyncChecked(ct);
        await WaitForGrpcReadyAsync(address, ct);

        try
        {
            var payloads = new List<string>();
            await foreach (var response in client.ServerStreamAsync(new StreamRequest { Value = "probe" }, cancellationToken: ct).WithCancellation(ct))
            {
                payloads.Add(response.ValueOrChecked().Body.Value);
            }

            payloads.Should().Equal("probe-0", "probe-1", "probe-2");
        }
        finally
        {
            await clientDispatcher.StopAsyncChecked(CancellationToken.None);
            await serverDispatcher.StopAsyncChecked(CancellationToken.None);
        }

        var serverSpan = activities.FirstOrDefault(activity =>
            string.Equals(activity.DisplayName, "grpc.server.server_stream", StringComparison.Ordinal));
        serverSpan.Should().NotBeNull();
        serverSpan!.Kind.Should().Be(ActivityKind.Server);
        serverSpan.GetTagItem("rpc.protocol").Should().Be("HTTP/2");
        serverSpan.GetTagItem("network.protocol.name").Should().Be("http");
        serverSpan.GetTagItem("network.protocol.version").Should().Be("2");
        serverSpan.GetTagItem("network.transport").Should().Be("tcp");
        (serverSpan.GetTagItem("net.peer.ip") as string).Should().NotBeNullOrWhiteSpace();
        serverSpan.Duration.Should().BeGreaterThan(TimeSpan.Zero);

        var clientSpan = activities.FirstOrDefault(activity =>
            string.Equals(activity.DisplayName, "grpc.client.server_stream", StringComparison.Ordinal));
        clientSpan.Should().NotBeNull();
        clientSpan!.Kind.Should().Be(ActivityKind.Client);
        clientSpan.GetTagItem("rpc.system").Should().Be("grpc");
        (clientSpan.GetTagItem("net.peer.ip") as string).Should().NotBeNullOrWhiteSpace();
        clientSpan.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    private static void RegisterPingProcedure(Dispatcher.Dispatcher dispatcher)
    {
        dispatcher.RegisterUnary("diagnostics::ping", builder =>
        {
            builder.WithEncoding(MediaTypeNames.Text.Plain);
            builder.Handle(static (request, _) =>
            {
                var responseMeta = new ResponseMeta(encoding: MediaTypeNames.Text.Plain);
                var payload = "pong"u8.ToArray();
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(payload, responseMeta)));
            });
        });
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        var resource = path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
        using var response = await client.GetAsync(resource, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(payload);
    }

    private static ActivityListener CreateGrpcActivityListener(ConcurrentBag<Activity> sink)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(
                source.Name,
                GrpcActivitySourceName,
                StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity is not null)
                {
                    sink.Add(activity);
                }
            }
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static async Task WaitForGrpcReadyAsync(Uri address, CancellationToken cancellationToken)
    {
        const int maxAttempts = 100;
        const int connectTimeoutMilliseconds = 200;
        const int settleDelayMilliseconds = 50;
        const int retryDelayMilliseconds = 20;

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
            }
            catch (TimeoutException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(retryDelayMilliseconds), cancellationToken);
        }

        throw new TimeoutException("The gRPC inbound failed to bind within the allotted time.");
    }

    private static async Task WaitForHttpReadyAsync(Uri baseAddress, CancellationToken cancellationToken)
    {
        const int maxAttempts = 100;
        const int retryDelayMilliseconds = 25;

        using var client = new HttpClient { BaseAddress = baseAddress };
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await client.GetAsync("/healthz", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(retryDelayMilliseconds), cancellationToken);
        }

        throw new TimeoutException($"HTTP inbound at {baseAddress} failed to respond to /healthz.");
    }

    private static void AssertScopeContains(LogEntry entry, string key, string expected)
    {
        var hasMatch = entry.Scope.Any(pair =>
            string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pair.Value?.ToString(), expected, StringComparison.OrdinalIgnoreCase));

        hasMatch.Should().BeTrue($"Expected scope to contain '{key}={expected}'.");
    }

    private static bool HasTag(MeasurementRecord measurement, string key, object expected)
    {
        return measurement.Tags.Any(tag =>
            string.Equals(tag.Key, key, StringComparison.OrdinalIgnoreCase) &&
            Equals(tag.Value, expected));
    }

    private sealed record MeasurementRecord(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)
    {
        public string Instrument { get; init; } = Instrument;

        public double Value { get; init; } = Value;

        public KeyValuePair<string, object?>[] Tags { get; init; } = Tags;
    }

    private sealed class HttpMetricCollector : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly ConcurrentBag<MeasurementRecord> _measurements = [];

        public HttpMetricCollector()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == HttpMeterName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                }
            };

            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                if (instrument.Meter.Name == HttpMeterName)
                {
                    _measurements.Add(new MeasurementRecord(instrument.Name, measurement, tags.ToArray()));
                }
            });

            _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            {
                if (instrument.Meter.Name == HttpMeterName)
                {
                    _measurements.Add(new MeasurementRecord(instrument.Name, measurement, tags.ToArray()));
                }
            });

            _listener.Start();
        }

        public IReadOnlyCollection<MeasurementRecord> Measurements => [.. _measurements];

        public void Dispose() => _listener.Dispose();
    }

    private sealed record LogEntry(
        string Category,
        LogLevel Level,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>>? State,
        IReadOnlyList<KeyValuePair<string, object?>> Scope)
    {
        public string Category { get; init; } = Category;

        public LogLevel Level { get; init; } = Level;

        public string Message { get; init; } = Message;

        public IReadOnlyList<KeyValuePair<string, object?>>? State { get; init; } = State;

        public IReadOnlyList<KeyValuePair<string, object?>> Scope { get; init; } = Scope;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();
        private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

        public IReadOnlyCollection<LogEntry> Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries, () => _scopeProvider);

        public void Dispose()
        {
        }

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
            _scopeProvider = scopeProvider ?? new LoggerExternalScopeProvider();

        private sealed class CapturingLogger(
            string category,
            ConcurrentQueue<LogEntry> sink,
            Func<IExternalScopeProvider> scopeAccessor)
            : ILogger
        {
            private readonly string _category = category ?? throw new ArgumentNullException(nameof(category));
            private readonly ConcurrentQueue<LogEntry> _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            private readonly Func<IExternalScopeProvider> _scopeAccessor = scopeAccessor ?? throw new ArgumentNullException(nameof(scopeAccessor));

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
                _scopeAccessor()?.Push(state) ?? NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (formatter is null)
                {
                    throw new ArgumentNullException(nameof(formatter));
                }

                var scopeItems = new List<KeyValuePair<string, object?>>();
                _scopeAccessor()?.ForEachScope((scope, list) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        list.AddRange(pairs);
                    }
                }, scopeItems);

                IReadOnlyList<KeyValuePair<string, object?>>? stateItems = null;
                if (state is IEnumerable<KeyValuePair<string, object?>> statePairs)
                {
                    stateItems = [.. statePairs];
                }

                var entry = new LogEntry(_category, logLevel, formatter(state, exception), stateItems, scopeItems);
                _sink.Enqueue(entry);
            }
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class StreamingProbeService : TestServiceOmniRelay.ITestService
    {
        public ValueTask<Response<UnaryResponse>> UnaryCallAsync(Request<UnaryRequest> request, CancellationToken cancellationToken)
        {
            var response = Response<UnaryResponse>.Create(
                new UnaryResponse { Message = $"{request.Body.Message}-ok" },
                new ResponseMeta(encoding: "protobuf"));
            return ValueTask.FromResult(response);
        }

        public async ValueTask ServerStreamAsync(
            Request<StreamRequest> request,
            ProtobufCallAdapters.ProtobufServerStreamWriter<StreamRequest, StreamResponse> stream,
            CancellationToken cancellationToken)
        {
            for (var index = 0; index < 3; index++)
            {
                var payload = new StreamResponse { Value = $"{request.Body.Value}-{index}" };
                var writeResult = await stream.WriteAsync(payload, cancellationToken);
                writeResult.ValueOrChecked();
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            }
        }

        public ValueTask<Response<UnaryResponse>> ClientStreamAsync(
            ProtobufCallAdapters.ProtobufClientStreamContext<StreamRequest, UnaryResponse> context,
            CancellationToken cancellationToken)
        {
            var response = Response<UnaryResponse>.Create(new UnaryResponse(), new ResponseMeta(encoding: "protobuf"));
            return ValueTask.FromResult(response);
        }

        public ValueTask DuplexStreamAsync(
            ProtobufCallAdapters.ProtobufDuplexStreamContext<StreamRequest, StreamResponse> context,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
