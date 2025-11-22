using Hugo;
using Hugo.Policies;

namespace OmniRelay.Core.Middleware;

/// <summary>
/// Configuration for the retry middleware, including policy and predicates.
/// </summary>
public sealed class RetryOptions
{
    public ResultExecutionPolicy Policy { get; init; } = ResultExecutionPolicy.None.WithRetry(
        ResultRetryPolicy.Exponential(
            3,
            TimeSpan.FromMilliseconds(50),
            2.0,
            TimeSpan.FromSeconds(1)));

    public Func<RequestMeta, ResultExecutionPolicy?>? PolicySelector { get; init; }

    public Func<RequestMeta, bool>? ShouldRetryRequest { get; init; }

    public Func<Error, bool>? ShouldRetryError { get; init; }

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
