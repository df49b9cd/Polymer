using System.Collections.Immutable;

namespace OmniRelay.Errors;

/// <summary>
/// Internal mapping for OmniRelay status code classifications, like fault type and retryability.
/// </summary>
internal static class OmniRelayStatusFacts
{
    private static readonly ImmutableDictionary<OmniRelayStatusCode, OmniRelayFaultType> FaultTypes =
        new[]
        {
            (OmniRelayStatusCode.Cancelled, OmniRelayFaultType.Client),
            (OmniRelayStatusCode.InvalidArgument, OmniRelayFaultType.Client),
            (OmniRelayStatusCode.DeadlineExceeded, OmniRelayFaultType.Client),
            (OmniRelayStatusCode.NotFound, OmniRelayFaultType.Client),
            (OmniRelayStatusCode.AlreadyExists, OmniRelayFaultType.Client),
            (OmniRelayStatusCode.PermissionDenied, OmniRelayFaultType.Client),
            (OmniRelayStatusCode.FailedPrecondition, OmniRelayFaultType.Client),
            (OmniRelayStatusCode.Aborted, OmniRelayFaultType.Client),
            (OmniRelayStatusCode.OutOfRange, OmniRelayFaultType.Client),
            (OmniRelayStatusCode.Internal, OmniRelayFaultType.Server),
            (OmniRelayStatusCode.Unavailable, OmniRelayFaultType.Server),
            (OmniRelayStatusCode.DataLoss, OmniRelayFaultType.Server),
            (OmniRelayStatusCode.Unimplemented, OmniRelayFaultType.Server),
            (OmniRelayStatusCode.ResourceExhausted, OmniRelayFaultType.Server)
        }.ToImmutableDictionary(static entry => entry.Item1, static entry => entry.Item2);

    private static readonly ImmutableHashSet<OmniRelayStatusCode> RetryableStatuses =
        ImmutableHashSet.Create(
            OmniRelayStatusCode.Unavailable,
            OmniRelayStatusCode.Internal,
            OmniRelayStatusCode.ResourceExhausted,
            OmniRelayStatusCode.Aborted,
            OmniRelayStatusCode.DeadlineExceeded);

    public static OmniRelayFaultType GetFaultType(OmniRelayStatusCode statusCode) =>
        FaultTypes.TryGetValue(statusCode, out var faultType)
            ? faultType
            : OmniRelayFaultType.Unknown;

    public static bool IsRetryable(OmniRelayStatusCode statusCode) =>
        RetryableStatuses.Contains(statusCode);
}
