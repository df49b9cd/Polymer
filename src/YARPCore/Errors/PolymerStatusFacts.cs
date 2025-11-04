using System.Collections.Immutable;

namespace YARPCore.Errors;

internal static class PolymerStatusFacts
{
    private static readonly ImmutableDictionary<PolymerStatusCode, PolymerFaultType> FaultTypes =
        new[]
        {
            (PolymerStatusCode.Cancelled, PolymerFaultType.Client),
            (PolymerStatusCode.InvalidArgument, PolymerFaultType.Client),
            (PolymerStatusCode.DeadlineExceeded, PolymerFaultType.Client),
            (PolymerStatusCode.NotFound, PolymerFaultType.Client),
            (PolymerStatusCode.AlreadyExists, PolymerFaultType.Client),
            (PolymerStatusCode.PermissionDenied, PolymerFaultType.Client),
            (PolymerStatusCode.FailedPrecondition, PolymerFaultType.Client),
            (PolymerStatusCode.Aborted, PolymerFaultType.Client),
            (PolymerStatusCode.OutOfRange, PolymerFaultType.Client),
            (PolymerStatusCode.Internal, PolymerFaultType.Server),
            (PolymerStatusCode.Unavailable, PolymerFaultType.Server),
            (PolymerStatusCode.DataLoss, PolymerFaultType.Server),
            (PolymerStatusCode.Unimplemented, PolymerFaultType.Server),
            (PolymerStatusCode.ResourceExhausted, PolymerFaultType.Server)
        }.ToImmutableDictionary(static entry => entry.Item1, static entry => entry.Item2);

    private static readonly ImmutableHashSet<PolymerStatusCode> RetryableStatuses =
        ImmutableHashSet.Create(
            PolymerStatusCode.Unavailable,
            PolymerStatusCode.Internal,
            PolymerStatusCode.ResourceExhausted,
            PolymerStatusCode.Aborted,
            PolymerStatusCode.DeadlineExceeded);

    public static PolymerFaultType GetFaultType(PolymerStatusCode statusCode) =>
        FaultTypes.TryGetValue(statusCode, out var faultType)
            ? faultType
            : PolymerFaultType.Unknown;

    public static bool IsRetryable(PolymerStatusCode statusCode) =>
        RetryableStatuses.Contains(statusCode);
}
