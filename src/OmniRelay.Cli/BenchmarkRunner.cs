using System.Collections.Concurrent;
using System.Diagnostics;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;

namespace OmniRelay.Cli;

internal sealed record RequestInvocation(
    string Transport,
    Request<ReadOnlyMemory<byte>> Request,
    TimeSpan? Timeout,
    string? HttpUrl,
    string[] Addresses,
    HttpClientRuntimeOptions? HttpClientRuntime,
    GrpcClientRuntimeOptions? GrpcClientRuntime);

internal static class BenchmarkRunner
{
    internal sealed record BenchmarkExecutionOptions(
        int Concurrency,
        long? MaxRequests,
        TimeSpan? Duration,
        double? RateLimitPerSecond,
        TimeSpan? WarmupDuration,
        TimeSpan PerRequestTimeout);

    internal sealed record LatencyStatistics(
        double Min,
        double P50,
        double P90,
        double P95,
        double P99,
        double Max,
        double Mean);

    internal sealed record BenchmarkSummary(
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
        IRequestInvoker invoker = invocation.Transport switch
        {
            "http" => CreateHttpInvoker(invocation),
            "grpc" => CreateGrpcInvoker(invocation),
            _ => throw new InvalidOperationException($"Transport '{invocation.Transport}' is not supported for benchmarking.")
        };

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

        return new HttpRequestInvoker(uri, invocation.HttpClientRuntime);
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

        return new GrpcRequestInvoker(uris, invocation.Request.Meta.Service, invocation.GrpcClientRuntime);
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

        var latencies = collectMetrics ? new ConcurrentBag<double>() : null;
        var errors = collectMetrics ? new ConcurrentDictionary<string, int>(StringComparer.Ordinal) : null;

        var scheduled = 0L;
        var completed = 0L;
        var successes = 0L;
        var failures = 0L;

        var stopwatch = Stopwatch.StartNew();
        var originTimestamp = Stopwatch.GetTimestamp();

        var tasks = new Task[parameters.Concurrency];
        for (var index = 0; index < tasks.Length; index++)
        {
            tasks[index] = Task.Run(async () =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (parameters.Duration is { } duration && stopwatch.Elapsed >= duration)
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
                        await WaitForRateLimitAsync(parameters.RateLimitPerSecond, issued - 1, originTimestamp, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    var sw = Stopwatch.StartNew();
                    CancellationTokenSource? callSource = null;
                    var callToken = cancellationToken;

                    if (parameters.PerRequestTimeout > TimeSpan.Zero)
                    {
                        callSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        callSource.CancelAfter(parameters.PerRequestTimeout);
                        callToken = callSource.Token;
                    }

                    RequestCallResult callResult;

                    try
                    {
                        callResult = await invoker.InvokeAsync(request, callToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        callSource?.Dispose();
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
                        callSource?.Dispose();
                    }

                    sw.Stop();

                    Interlocked.Increment(ref completed);

                    if (callResult.Success)
                    {
                        Interlocked.Increment(ref successes);
                        latencies?.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    else
                    {
                        Interlocked.Increment(ref failures);
                        if (collectMetrics)
                        {
                            var label = string.IsNullOrWhiteSpace(callResult.Error)
                                ? "error"
                                : callResult.Error!;
                            errors!.AddOrUpdate(label, 1, static (_, current) => current + 1);
                        }
                    }

                    if (parameters.Duration is { } remaining && stopwatch.Elapsed >= remaining)
                    {
                        break;
                    }

                    if (parameters.MaxRequests.HasValue && Interlocked.Read(ref completed) >= parameters.MaxRequests.Value)
                    {
                        break;
                    }
                }
            }, CancellationToken.None);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        stopwatch.Stop();

        var latencySamples = collectMetrics
            ? latencies!.ToList()
            : [];

        var errorSummary = collectMetrics
            ? new Dictionary<string, int>(errors!, StringComparer.Ordinal)
            : new Dictionary<string, int>(StringComparer.Ordinal);

        return new StageResult(
            Interlocked.Read(ref completed),
            Interlocked.Read(ref successes),
            Interlocked.Read(ref failures),
            latencySamples,
            errorSummary,
            stopwatch.Elapsed);
    }

    private static async ValueTask WaitForRateLimitAsync(double? rateLimit, long requestIndex, long originTimestamp, CancellationToken cancellationToken)
    {
        if (!rateLimit.HasValue || rateLimit.Value <= 0 || requestIndex <= 0)
        {
            return;
        }

        var targetSeconds = requestIndex / rateLimit.Value;
        var targetTicks = originTimestamp + (long)(targetSeconds * Stopwatch.Frequency);

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
        var polymerException = OmniRelayErrors.FromError(error, transport);
        return $"{polymerException.StatusCode}: {polymerException.Message}";
    }

    private interface IRequestInvoker : IAsyncDisposable
    {
        Task StartAsync(CancellationToken cancellationToken);
        Task<RequestCallResult> InvokeAsync(Request<ReadOnlyMemory<byte>> request, CancellationToken cancellationToken);
    }

    private sealed class HttpRequestInvoker : IRequestInvoker
    {
        private readonly HttpClient _httpClient;
        private readonly HttpOutbound _outbound;
        private readonly IUnaryOutbound _unaryOutbound;

        public HttpRequestInvoker(Uri requestUri, HttpClientRuntimeOptions? runtimeOptions)
        {
            _httpClient = new HttpClient();
            _outbound = new HttpOutbound(_httpClient, requestUri, runtimeOptions: runtimeOptions);
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

    private sealed class GrpcRequestInvoker : IRequestInvoker
    {
        private readonly GrpcOutbound _outbound;
        private readonly IUnaryOutbound _unaryOutbound;

        public GrpcRequestInvoker(IReadOnlyList<Uri> addresses, string service, GrpcClientRuntimeOptions? runtimeOptions)
        {
            _outbound = new GrpcOutbound(addresses, service, clientRuntimeOptions: runtimeOptions);
            _unaryOutbound = _outbound;
        }

        public Task StartAsync(CancellationToken cancellationToken) =>
            _outbound.StartAsync(cancellationToken).AsTask();

        public async Task<RequestCallResult> InvokeAsync(Request<ReadOnlyMemory<byte>> request, CancellationToken cancellationToken)
        {
            var result = await _outbound.CallAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                return RequestCallResult.FromSuccess();
            }

            return RequestCallResult.FromFailure(FormatError(result.Error!, "grpc"));
        }

        public async ValueTask DisposeAsync() => await _outbound.StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private readonly record struct RequestCallResult(bool Success, string? Error)
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
