using System.Collections.Immutable;
using Hugo;

namespace YARPCore.Errors;

public static class PolymerErrorAdapter
{
    internal const string StatusMetadataKey = "yarpcore.status";
    internal const string TransportMetadataKey = "yarpcore.transport";
    internal const string FaultMetadataKey = "yarpcore.faultType";
    internal const string RetryableMetadataKey = "yarpcore.retryable";
    private static readonly ImmutableDictionary<PolymerStatusCode, string> StatusCodeNames = new[]
    {
        (PolymerStatusCode.Unknown, "unknown"),
        (PolymerStatusCode.Cancelled, "cancelled"),
        (PolymerStatusCode.InvalidArgument, "invalid-argument"),
        (PolymerStatusCode.DeadlineExceeded, "deadline-exceeded"),
        (PolymerStatusCode.NotFound, "not-found"),
        (PolymerStatusCode.AlreadyExists, "already-exists"),
        (PolymerStatusCode.PermissionDenied, "permission-denied"),
        (PolymerStatusCode.ResourceExhausted, "resource-exhausted"),
        (PolymerStatusCode.FailedPrecondition, "failed-precondition"),
        (PolymerStatusCode.Aborted, "aborted"),
        (PolymerStatusCode.OutOfRange, "out-of-range"),
        (PolymerStatusCode.Unimplemented, "unimplemented"),
        (PolymerStatusCode.Internal, "internal"),
        (PolymerStatusCode.Unavailable, "unavailable"),
        (PolymerStatusCode.DataLoss, "data-loss")
    }.ToImmutableDictionary(static tuple => tuple.Item1, static tuple => tuple.Item2);

    public static Error FromStatus(
        PolymerStatusCode code,
        string message,
        string? transport = null,
        Error? inner = null,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var error = inner ?? Error.From(message, StatusCodeNames[code]);
        error = AnnotateCoreMetadata(error, code);

        if (!string.IsNullOrEmpty(transport))
        {
            error = error.WithMetadata(TransportMetadataKey, transport);
        }

        if (metadata is { Count: > 0 })
        {
            foreach (var kvp in metadata)
            {
                error = error.WithMetadata(kvp.Key, kvp.Value);
            }
        }

        return error;
    }

    public static PolymerStatusCode ToStatus(Error error)
    {
        if (error.TryGetMetadata(StatusMetadataKey, out string? value) &&
            Enum.TryParse<PolymerStatusCode>(value, out var parsed))
        {
            return parsed;
        }

        if (!string.IsNullOrEmpty(error.Code))
        {
            foreach (var (status, codeName) in StatusCodeNames)
            {
                if (string.Equals(error.Code, codeName, StringComparison.OrdinalIgnoreCase))
                {
                    return status;
                }
            }
        }

        if (error.Cause is OperationCanceledException)
        {
            return PolymerStatusCode.Cancelled;
        }

        return PolymerStatusCode.Unknown;
    }

    public static Error WithStatusMetadata(Error error, PolymerStatusCode code) =>
        AnnotateCoreMetadata(error, code);

    internal static string GetStatusName(PolymerStatusCode code) => StatusCodeNames[code];

    private static Error AnnotateCoreMetadata(Error error, PolymerStatusCode code)
    {
        var updated = error.WithCode(StatusCodeNames[code]).WithMetadata(StatusMetadataKey, code.ToString());

        if (!updated.TryGetMetadata(FaultMetadataKey, out string? _))
        {
            var fault = PolymerStatusFacts.GetFaultType(code);
            if (fault != PolymerFaultType.Unknown)
            {
                updated = updated.WithMetadata(FaultMetadataKey, fault.ToString());
            }
        }

        if (!updated.TryGetMetadata(RetryableMetadataKey, out bool _))
        {
            updated = updated.WithMetadata(RetryableMetadataKey, PolymerStatusFacts.IsRetryable(code));
        }

        return updated;
    }
}
