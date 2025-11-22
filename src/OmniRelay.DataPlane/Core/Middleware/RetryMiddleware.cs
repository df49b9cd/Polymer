using Hugo;
using Hugo.Policies;
using OmniRelay.Core.Peers;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;

namespace OmniRelay.Core.Middleware;

#pragma warning disable CA1068 // CancellationToken parameter precedes delegate for OmniRelay middleware contract.

/// <summary>
/// Retries failed unary outbound requests based on a configurable policy and predicates.
/// </summary>
public sealed class RetryMiddleware(RetryOptions? options = null) : IUnaryOutboundMiddleware
{
    private const string RetryAbortMetadataKey = "omni.retry.abort";
    private readonly RetryOptions _options = options ?? new RetryOptions();

    /// <inheritdoc />
    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundHandler nextHandler) =>
        ExecuteWithRetryAsync(request, cancellationToken, nextHandler);

    private async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> ExecuteWithRetryAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundHandler next)
    {
        var meta = request.Meta;

        if (_options.ShouldRetryRequest is { } requestPredicate && !requestPredicate(meta))
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }

        var policy = ResolvePolicy(meta);

        if (_options.ShouldRetryError is not null)
        {
            return await ExecuteWithCustomPredicateAsync(request, cancellationToken, next, meta, policy).ConfigureAwait(false);
        }

        return await ExecuteWithPolicyAsync(request, cancellationToken, next, meta, policy).ConfigureAwait(false);
    }

    private ResultExecutionPolicy ResolvePolicy(RequestMeta meta)
    {
        if (_options.PolicySelector is { } selector && selector(meta) is { } selected)
        {
            return selected;
        }

        return _options.Policy ?? ResultExecutionPolicy.None;
    }

    private bool IsRetryable(Error error)
    {
        if (_options.ShouldRetryError is { } predicate)
        {
            return predicate(error);
        }

        return OmniRelayErrors.IsRetryable(error);
    }

    private async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> ExecuteWithPolicyAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundHandler next,
        RequestMeta meta,
        ResultExecutionPolicy policy)
    {
        if (policy.Retry == ResultRetryPolicy.None)
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }

        var timeProvider = _options.TimeProvider ?? TimeProvider.System;
        var attempt = 0;
        var retryPolicy = policy.Retry;
        RetryState? instrumentationState = null;
        if (retryPolicy is not null && retryPolicy != ResultRetryPolicy.None)
        {
            instrumentationState = retryPolicy.CreateState(timeProvider);
        }
        else
        {
            retryPolicy = null;
        }

        var result = await Result.RetryWithPolicyAsync<Response<ReadOnlyMemory<byte>>>(
            async (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                attempt++;
                var attemptResult = await next(request, token).ConfigureAwait(false);

                if (attemptResult.IsSuccess && attempt > 1)
                {
                    PeerMetrics.RecordRetrySucceeded(meta, attempt);
                }

                if (attemptResult.IsFailure)
                {
                    var error = attemptResult.Error!;
                    if (!IsRetryable(error))
                    {
                        return Result.Fail<Response<ReadOnlyMemory<byte>>>(WrapNonRetryable(error));
                    }

                    if (retryPolicy is not null && instrumentationState is not null)
                    {
                        var decision = await retryPolicy.EvaluateAsync(instrumentationState, error, token).ConfigureAwait(false);
                        if (decision.ShouldRetry)
                        {
                            var delay = ResolveRetryDelay(decision, timeProvider);
                            PeerMetrics.RecordRetryScheduled(meta, error, attempt, delay);
                        }
                    }
                }

                return attemptResult;
            },
            policy,
            timeProvider,
            cancellationToken).ConfigureAwait(false);

        if (result.IsFailure && TryUnwrapNonRetryable(result.Error!, out var original))
        {
            return Result.Fail<Response<ReadOnlyMemory<byte>>>(original);
        }

        if (result.IsFailure && attempt > 1)
        {
            PeerMetrics.RecordRetryExhausted(meta, result.Error!, attempt);
        }

        return result;
    }

    private async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> ExecuteWithCustomPredicateAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundHandler next,
        RequestMeta meta,
        ResultExecutionPolicy policy)
    {
        var retry = policy.Retry ?? ResultRetryPolicy.None;
        if (retry == ResultRetryPolicy.None)
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }

        var timeProvider = _options.TimeProvider ?? TimeProvider.System;
        var state = retry.CreateState(timeProvider);
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            attempt++;
            var attemptResult = await next(request, cancellationToken).ConfigureAwait(false);
            if (attemptResult.IsSuccess)
            {
                if (attempt > 1)
                {
                    PeerMetrics.RecordRetrySucceeded(meta, attempt);
                }
                return attemptResult;
            }

            var error = attemptResult.Error!;
            if (!IsRetryable(error))
            {
                if (attempt > 1)
                {
                    PeerMetrics.RecordRetryExhausted(meta, error, attempt);
                }
                return attemptResult;
            }

            var decision = await retry.EvaluateAsync(state, error, cancellationToken).ConfigureAwait(false);
            if (!decision.ShouldRetry)
            {
                PeerMetrics.RecordRetryExhausted(meta, error, attempt);
                return attemptResult;
            }

            var delay = decision.Delay;
            if (delay is null && decision.ScheduledAt is { } scheduledAt)
            {
                var delta = scheduledAt - timeProvider.GetUtcNow();
                if (delta > TimeSpan.Zero)
                {
                    delay = delta;
                }
            }

            PeerMetrics.RecordRetryScheduled(meta, error, attempt, delay);
            if (delay is { } sleep && sleep > TimeSpan.Zero)
            {
                await Task.Delay(sleep, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan? ResolveRetryDelay(RetryDecision decision, TimeProvider timeProvider)
    {
        if (decision.Delay is { } delay)
        {
            return delay;
        }

        if (decision.ScheduledAt is not null)
        {
            var delta = decision.ScheduledAt.Value - timeProvider.GetUtcNow();
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        return null;
    }

    private static Error WrapNonRetryable(Error error) =>
        Error.Canceled("retry aborted").WithMetadata(RetryAbortMetadataKey, error);

    private static bool TryUnwrapNonRetryable(Error? error, out Error original)
    {
        original = null!;
        if (error?.Code == ErrorCodes.Canceled &&
            error.Metadata.TryGetValue(RetryAbortMetadataKey, out var value) &&
            value is Error typed)
        {
            original = typed;
            return true;
        }

        return false;
    }
}

#pragma warning restore CA1068
