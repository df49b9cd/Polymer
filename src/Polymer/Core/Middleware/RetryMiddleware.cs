using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using Hugo.Policies;
using Polymer.Core;
using Polymer.Core.Peers;
using Polymer.Core.Transport;
using Polymer.Errors;
using static Hugo.Go;

namespace Polymer.Core.Middleware;

public sealed class RetryMiddleware : IUnaryOutboundMiddleware
{
    private static readonly ImmutableHashSet<PolymerStatusCode> DefaultRetryableStatuses =
        ImmutableHashSet.Create(
            PolymerStatusCode.Unavailable,
            PolymerStatusCode.Internal,
            PolymerStatusCode.ResourceExhausted,
            PolymerStatusCode.Aborted,
            PolymerStatusCode.DeadlineExceeded);

    private readonly RetryOptions _options;

    public RetryMiddleware(RetryOptions? options = null)
    {
        _options = options ?? new RetryOptions();
    }

    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundDelegate next) =>
        ExecuteWithRetryAsync(request, cancellationToken, next);

    private async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> ExecuteWithRetryAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryOutboundDelegate next)
    {
        var meta = request.Meta;

        if (_options.ShouldRetryRequest is { } requestPredicate && !requestPredicate(meta))
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }

        var policy = ResolvePolicy(meta);
        if (policy.Retry == ResultRetryPolicy.None)
        {
            return await next(request, cancellationToken).ConfigureAwait(false);
        }

        var retry = policy.Retry ?? ResultRetryPolicy.None;
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

        var status = PolymerErrorAdapter.ToStatus(error);
        return DefaultRetryableStatuses.Contains(status);
    }
}
