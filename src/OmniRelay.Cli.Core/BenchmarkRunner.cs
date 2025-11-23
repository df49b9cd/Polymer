using System.Diagnostics;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;

namespace OmniRelay.Cli.Core;

public sealed record RequestInvocation(
    string Transport,
    Request<ReadOnlyMemory<byte>> Request,
    TimeSpan? Timeout,
    string? HttpUrl,
    string[] Addresses,
    HttpClientRuntimeOptions? HttpClientRuntime,
    GrpcClientRuntimeOptions? GrpcClientRuntime);

public static class BenchmarkRunner
{
    // Host must provide these factories; defaults throw to avoid accidental nulls.
    public static Func<HttpClient> HttpClientFactory { get; set; } = static () => throw new InvalidOperationException("Benchmark HTTP client factory is not configured.");
    public static Func<IReadOnlyList<Uri>, string, GrpcClientRuntimeOptions?, IGrpcInvoker> GrpcInvokerFactory { get; set; } = static (_, _, _) => throw new InvalidOperationException("Benchmark gRPC invoker factory is not configured.");

    public static Func<RequestInvocation, CancellationToken, Task<IRequestInvoker>>? InvokerFactoryOverride { get; set; }

    public static void ResetForTests() => InvokerFactoryOverride = null;

    public sealed record BenchmarkExecutionOptions(
        int Concurrency,
        long? MaxRequests,
        TimeSpan? Duration,
        double? RateLimitPerSecond,
        TimeSpan? WarmupDuration,
        TimeSpan PerRequestTimeout);

    public sealed record LatencyStatistics(
        double Min,
        double P50,
        double P90,
        double P95,
        double P99,
        double Max,
        double Mean);

    public sealed record BenchmarkSummary(
        long Attempts,
        long Successes,
        long Failures,
        TimeSpan Elapsed,
        double RequestsPerSecond,
        LatencyStatistics? Latency,
        IReadOnlyDictionary<string, int> Errors);

    public static async Task<BenchmarkSummary> RunAsync(
        RequestInvocation invocation,
        BenchmarkExecutionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(options);

        var invoker = await CreateInvokerAsync(invocation, cancellationToken).ConfigureAwait(false);

        try
        {
            if (options.WarmupDuration is { TotalMilliseconds: > 0 } warmupDuration)
            {
                var warmupParameters = new StageParameters(
                    options.Concurrency,
                    null,
                    warmupDuration,
                    options.RateLimitPerSecond,
                    options.PerRequestTimeout);

                await RunStageAsync(invocation.Request, invoker, warmupParameters, collectMetrics: false, cancellationToken).ConfigureAwait(false);
            }

            var measurementParameters = new StageParameters(
                options.Concurrency,
                options.MaxRequests,
                options.Duration,
                options.RateLimitPerSecond,
                options.PerRequestTimeout);

            var measurement = await RunStageAsync(invocation.Request, invoker, measurementParameters, collectMetrics: true, cancellationToken).ConfigureAwait(false);

            var latency = measurement.Latencies.Count > 0
                ? ComputeLatency(measurement.Latencies)
                : null;

            var elapsedSeconds = measurement.Elapsed.TotalSeconds;
            var rps = elapsedSeconds > 0.0
                ? measurement.Attempts / elapsedSeconds
                : 0.0;

            return new BenchmarkSummary(
                measurement.Attempts,
                measurement.Successes,
                measurement.Failures,
                measurement.Elapsed,
                rps,
                latency,
                measurement.Errors);
        }
        finally
        {
            await invoker.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<IRequestInvoker> CreateInvokerAsync(RequestInvocation invocation, CancellationToken cancellationToken)
    {
        IRequestInvoker invoker;
        if (InvokerFactoryOverride is { } overrideFactory)
        {
            invoker = await overrideFactory(invocation, cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException("Benchmark invoker override returned null.");
        }
        else
        {
            invoker = invocation.Transport switch
            {
                "http" => CreateHttpInvoker(invocation),
                "grpc" => CreateGrpcInvoker(invocation),
                _ => throw new InvalidOperationException($"Transport '{invocation.Transport}' is not supported for benchmarking.")
            };
        }

        await invoker.StartAsync(cancellationToken).ConfigureAwait(false);
        return invoker;
    }

    private static HttpRequestInvoker CreateHttpInvoker(RequestInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(invocation.HttpUrl))
        {
            throw new InvalidOperationException("HTTP benchmarking requires a target --url.");
        }

        if (!Uri.TryCreate(invocation.HttpUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Invalid HTTP url '{invocation.HttpUrl}'.");
        }

        return new HttpRequestInvoker(uri, invocation.HttpClientRuntime, HttpClientFactory);
    }

    private static GrpcRequestInvoker CreateGrpcInvoker(RequestInvocation invocation)
    {
        if (invocation.Addresses is not { Length: > 0 })
        {
            throw new InvalidOperationException("gRPC benchmarking requires at least one --address.");
        }

        var uris = new Uri[invocation.Addresses.Length];
        for (var index = 0; index < invocation.Addresses.Length; index++)
        {
            var address = invocation.Addresses[index];
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                throw new InvalidOperationException($"Invalid gRPC address '{address}'.");
            }

            uris[index] = uri;
        }

        return new GrpcRequestInvoker(
            uris,
            invocation.Request.Meta.Service,
            invocation.GrpcClientRuntime,
            GrpcInvokerFactory);
    }

    private static async Task<StageResult> RunStageAsync(
        Request<ReadOnlyMemory<byte>> request,
        IRequestInvoker invoker,
        StageParameters parameters,
        bool collectMetrics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentNullException.ThrowIfNull(parameters);

        if (parameters.Concurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), parameters.Concurrency, "Concurrency must be greater than zero.");
        }

        var latencies = collectMetrics ? new List<double>[parameters.Concurrency] : null;
        var errors = collectMetrics ? new Dictionary<string, int>[parameters.Concurrency] : null;

        var scheduled = 0L;
        var completed = 0L;
        var successes = 0L;
        var failures = 0L;

        var originTimestamp = Stopwatch.GetTimestamp();
        var stopwatch = Stopwatch.StartNew();
        long? durationDeadline = parameters.Duration is { TotalMilliseconds: > 0 }
            ? originTimestamp + (long)(parameters.Duration.Value.TotalSeconds * Stopwatch.Frequency)
            : null;
        var rateLimitTicksPerRequest = parameters.RateLimitPerSecond is { } limit and > 0
            ? Stopwatch.Frequency / limit
            : null as double?;

        var tasks = new Task[parameters.Concurrency];
        for (var index = 0; index < tasks.Length; index++)
        {
            var workerIndex = index;
            var workerLatencies = collectMetrics ? new List<double>(256) : null;
            var workerErrors = collectMetrics ? new Dictionary<string, int>(StringComparer.Ordinal) : null;

            if (collectMetrics)
            {
                latencies![workerIndex] = workerLatencies!;
                errors![workerIndex] = workerErrors!;
            }

            tasks[index] = Task.Run(async () =>
            {
                var timeoutScope = parameters.PerRequestTimeout > TimeSpan.Zero
                    ? new PerRequestTimeoutScope(parameters.PerRequestTimeout, cancellationToken)
                    : null;

                try
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        if (durationDeadline.HasValue && Stopwatch.GetTimestamp() >= durationDeadline.Value)
                        {
                            break;
                        }

                        var issued = Interlocked.Increment(ref scheduled);
                        if (parameters.MaxRequests.HasValue && issued > parameters.MaxRequests.Value)
                        {
                            break;
                        }

                        try
                        {
                            await WaitForRateLimitAsync(rateLimitTicksPerRequest, issued - 1, originTimestamp, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        var sw = Stopwatch.StartNew();
                        var callToken = timeoutScope?.Rent(cancellationToken) ?? cancellationToken;

                        RequestCallResult callResult;

                        try
                        {
                            callResult = await invoker.InvokeAsync(request, callToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            callResult = RequestCallResult.FromFailure("request timed out");
                        }
                        catch (Exception ex)
                        {
                            callResult = RequestCallResult.FromFailure(ex.Message);
                        }
                        finally
                        {
                            timeoutScope?.ResetTimer();
                        }

                        sw.Stop();

                        Interlocked.Increment(ref completed);

                        if (callResult.Success)
                        {
                            Interlocked.Increment(ref successes);
                            workerLatencies?.Add(sw.Elapsed.TotalMilliseconds);
                        }
                        else
                        {
                            Interlocked.Increment(ref failures);
                            if (collectMetrics && workerErrors is not null)
                            {
                                var label = string.IsNullOrWhiteSpace(callResult.Error)
                                    ? "error"
                                    : callResult.Error!;
                                if (!workerErrors.TryAdd(label, 1))
                                {
                                    workerErrors[label] = workerErrors[label] + 1;
                                }
                            }
                        }

                        if (durationDeadline.HasValue && Stopwatch.GetTimestamp() >= durationDeadline.Value)
                        {
                            break;
                        }

                        if (parameters.MaxRequests.HasValue && Interlocked.Read(ref completed) >= parameters.MaxRequests.Value)
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    timeoutScope?.Dispose();
                }
            }, CancellationToken.None);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        stopwatch.Stop();

        var latencySamples = collectMetrics && latencies is not null
            ? CombineLatencySamples(latencies)
            : [];

        var errorSummary = collectMetrics && errors is not null
            ? CombineErrors(errors)
            : new Dictionary<string, int>(StringComparer.Ordinal);

        return new StageResult(
            Interlocked.Read(ref completed),
            Interlocked.Read(ref successes),
            Interlocked.Read(ref failures),
            latencySamples,
            errorSummary,
            stopwatch.Elapsed);
    }

    private static List<double> CombineLatencySamples(List<double>[] perWorker)
    {
        var total = 0;
        for (var i = 0; i < perWorker.Length; i++)
        {
            total += perWorker[i].Count;
        }

        var combined = new List<double>(total);
        for (var i = 0; i < perWorker.Length; i++)
        {
            combined.AddRange(perWorker[i]);
        }

        return combined;
    }

    private static Dictionary<string, int> CombineErrors(Dictionary<string, int>[] perWorker)
    {
        var combined = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < perWorker.Length; i++)
        {
            foreach (var kvp in perWorker[i])
            {
                if (combined.TryGetValue(kvp.Key, out var current))
                {
                    combined[kvp.Key] = current + kvp.Value;
                }
                else
                {
                    combined[kvp.Key] = kvp.Value;
                }
            }
        }

        return combined;
    }

    private static async ValueTask WaitForRateLimitAsync(double? rateLimitTicksPerRequest, long requestIndex, long originTimestamp, CancellationToken cancellationToken)
    {
        if (!rateLimitTicksPerRequest.HasValue || requestIndex <= 0)
        {
            return;
        }

        var targetTicks = originTimestamp + (long)(requestIndex * rateLimitTicksPerRequest.Value);

        while (true)
        {
            var currentTicks = Stopwatch.GetTimestamp();
            var remainingTicks = targetTicks - currentTicks;

            if (remainingTicks <= 0)
            {
                return;
            }

            var remainingMilliseconds = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
            if (remainingMilliseconds > 1)
            {
                await Task.Delay(remainingMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }
        }
    }

    private static LatencyStatistics ComputeLatency(List<double> samples)
    {
        samples.Sort();

        var min = samples[0];
        var max = samples[^1];
        var mean = samples.Sum() / samples.Count;

        return new LatencyStatistics(
            min,
            GetPercentile(samples, 0.50),
            GetPercentile(samples, 0.90),
            GetPercentile(samples, 0.95),
            GetPercentile(samples, 0.99),
            max,
            mean);
    }

    private static double GetPercentile(List<double> samples, double percentile)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        if (samples.Count == 1)
        {
            return samples[0];
        }

        var rank = percentile * (samples.Count - 1);
        var lowerIndex = (int)Math.Floor(rank);
        var upperIndex = (int)Math.Ceiling(rank);

        if (lowerIndex == upperIndex)
        {
            return samples[lowerIndex];
        }

        var fraction = rank - lowerIndex;
        return samples[lowerIndex] + (samples[upperIndex] - samples[lowerIndex]) * fraction;
    }

    private static string FormatError(Error error, string transport)
    {
        var omnirelayException = OmniRelayErrors.FromError(error, transport);
        return $"{omnirelayException.StatusCode}: {omnirelayException.Message}";
    }

    private sealed class PerRequestTimeoutScope : IDisposable
    {
        private readonly TimeSpan _timeout;
        private readonly CancellationToken _outerToken;
        private CancellationTokenSource? _timeoutCts;
        private CancellationTokenRegistration _outerRegistration;

        public PerRequestTimeoutScope(TimeSpan timeout, CancellationToken outerToken)
        {
            _timeout = timeout;
            _outerToken = outerToken;

            _timeoutCts = new CancellationTokenSource();
            _outerRegistration = outerToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), _timeoutCts);
        }

        public CancellationToken Rent(CancellationToken fallback)
        {
            if (_timeoutCts is null)
            {
                return fallback;
            }

            if (_timeoutCts.IsCancellationRequested && !_timeoutCts.TryReset())
            {
                Recreate();
            }

            _timeoutCts.CancelAfter(_timeout);
            return _timeoutCts.Token;
        }

        public void ResetTimer() => _timeoutCts?.CancelAfter(Timeout.InfiniteTimeSpan);

        public void Dispose()
        {
            _outerRegistration.Dispose();
            _timeoutCts?.Dispose();
        }

        private void Recreate()
        {
            _outerRegistration.Dispose();
            _timeoutCts?.Dispose();

            _timeoutCts = new CancellationTokenSource();
            _outerRegistration = _outerToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), _timeoutCts);
        }
    }

    public interface IRequestInvoker : IAsyncDisposable
    {
        Task StartAsync(CancellationToken cancellationToken);
        Task<RequestCallResult> InvokeAsync(Request<ReadOnlyMemory<byte>> request, CancellationToken cancellationToken);
    }

    private sealed class HttpRequestInvoker : IRequestInvoker
    {
        private readonly HttpClient _httpClient;
        private readonly HttpOutbound _outbound;
        private readonly IUnaryOutbound _unaryOutbound;

        public HttpRequestInvoker(Uri requestUri, HttpClientRuntimeOptions? runtimeOptions, Func<HttpClient> httpClientFactory)
        {
            _httpClient = httpClientFactory();
            var outbound = HttpOutbound.Create(_httpClient, requestUri, runtimeOptions: runtimeOptions);
            if (outbound.IsFailure)
            {
                _httpClient.Dispose();
                throw new InvalidOperationException(outbound.Error?.Message ?? "Failed to create HttpOutbound.");
            }

            _outbound = outbound.Value;
            _unaryOutbound = _outbound;
        }

        public Task StartAsync(CancellationToken cancellationToken) =>
            _outbound.StartAsync(cancellationToken).AsTask();

        public async Task<RequestCallResult> InvokeAsync(Request<ReadOnlyMemory<byte>> request, CancellationToken cancellationToken)
        {
            var result = await _unaryOutbound.CallAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                return RequestCallResult.FromSuccess();
            }

            return RequestCallResult.FromFailure(FormatError(result.Error!, "http"));
        }

        public async ValueTask DisposeAsync()
        {
            await _outbound.StopAsync(CancellationToken.None).ConfigureAwait(false);
            _httpClient.Dispose();
        }
    }

    public interface IGrpcInvoker : IAsyncDisposable
    {
        ValueTask StartAsync(CancellationToken cancellationToken);
        ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(Request<ReadOnlyMemory<byte>> request, CancellationToken cancellationToken);
        ValueTask StopAsync(CancellationToken cancellationToken);
    }

    private sealed class GrpcRequestInvoker(
        IReadOnlyList<Uri> addresses,
        string service,
        GrpcClientRuntimeOptions? runtimeOptions,
        Func<IReadOnlyList<Uri>, string, GrpcClientRuntimeOptions?, IGrpcInvoker> invokerFactory)
        : IRequestInvoker
    {
        private readonly IGrpcInvoker _invoker = invokerFactory(addresses, service, runtimeOptions);

        public Task StartAsync(CancellationToken cancellationToken) =>
            _invoker.StartAsync(cancellationToken).AsTask();

        public async Task<RequestCallResult> InvokeAsync(Request<ReadOnlyMemory<byte>> request, CancellationToken cancellationToken)
        {
            var result = await _invoker.CallAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                return RequestCallResult.FromSuccess();
            }

            return RequestCallResult.FromFailure(FormatError(result.Error!, "grpc"));
        }

        public async ValueTask DisposeAsync()
        {
            await _invoker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _invoker.DisposeAsync().ConfigureAwait(false);
        }
    }

    public readonly record struct RequestCallResult(bool Success, string? Error)
    {
        public static RequestCallResult FromSuccess() => new(true, null);
        public static RequestCallResult FromFailure(string? error) => new(false, error);
    }

    private sealed record StageParameters(
        int Concurrency,
        long? MaxRequests,
        TimeSpan? Duration,
        double? RateLimitPerSecond,
        TimeSpan PerRequestTimeout);

    private sealed record StageResult(
        long Attempts,
        long Successes,
        long Failures,
        List<double> Latencies,
        Dictionary<string, int> Errors,
        TimeSpan Elapsed);
}
