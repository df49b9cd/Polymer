using OmniRelay.Errors;

namespace OmniRelay.Core.Middleware;

public sealed class DeadlineOptions
{
    /// <summary>
    /// Gets or sets a minimum lead time required between the current time and the requested deadline. If the deadline
    /// is closer than this threshold, the middleware immediately fails with <see cref="OmniRelayStatusCode.DeadlineExceeded"/>.
    /// Defaults to <c>null</c>, meaning no additional lead time is required.
    /// </summary>
    public TimeSpan? MinimumLeadTime { get; init; }
}
