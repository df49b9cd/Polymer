using System.Collections.Immutable;
using Hugo;

namespace OmniRelay.Errors;

/// <summary>
/// Adapts between OmniRelay status codes and Hugo <c>Error</c> objects with standardized metadata.
/// </summary>
public static class OmniRelayErrorAdapter
{
    public const string TransportMetadataKey = "omnirelay.transport";
    internal const string StatusMetadataKey = "omnirelay.status";
    internal const string FaultMetadataKey = "omnirelay.faultType";
    internal const string RetryableMetadataKey = "omnirelay.retryable";
    private static readonly ImmutableDictionary<OmniRelayStatusCode, string> StatusCodeNames = new[]
    {
        (OmniRelayStatusCode.Unknown, "unknown"),
        (OmniRelayStatusCode.Cancelled, "cancelled"),
        (OmniRelayStatusCode.InvalidArgument, "invalid-argument"),
        (OmniRelayStatusCode.DeadlineExceeded, "deadline-exceeded"),
        (OmniRelayStatusCode.NotFound, "not-found"),
        (OmniRelayStatusCode.AlreadyExists, "already-exists"),
        (OmniRelayStatusCode.PermissionDenied, "permission-denied"),
        (OmniRelayStatusCode.ResourceExhausted, "resource-exhausted"),
        (OmniRelayStatusCode.FailedPrecondition, "failed-precondition"),
        (OmniRelayStatusCode.Aborted, "aborted"),
        (OmniRelayStatusCode.OutOfRange, "out-of-range"),
        (OmniRelayStatusCode.Unimplemented, "unimplemented"),
        (OmniRelayStatusCode.Internal, "internal"),
        (OmniRelayStatusCode.Unavailable, "unavailable"),
        (OmniRelayStatusCode.DataLoss, "data-loss")
    }.ToImmutableDictionary(static tuple => tuple.Item1, static tuple => tuple.Item2);

    /// <summary>
    /// Creates a Hugo <see cref="Error"/> from an OmniRelay status and message, annotating metadata and transport.
    /// </summary>
    public static Error FromStatus(
        OmniRelayStatusCode code,
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

    /// <summary>
    /// Maps a Hugo <see cref="Error"/> back to an <see cref="OmniRelayStatusCode"/> using metadata, code, or cause.
    /// </summary>
    public static OmniRelayStatusCode ToStatus(Error error)
    {
        if (error.TryGetMetadata(StatusMetadataKey, out string? value) &&
            Enum.TryParse<OmniRelayStatusCode>(value, out var parsed))
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
            return OmniRelayStatusCode.Cancelled;
        }

        return OmniRelayStatusCode.Unknown;
    }

    /// <summary>Adds standardized status metadata to an error if missing.</summary>
    public static Error WithStatusMetadata(Error error, OmniRelayStatusCode code) =>
        AnnotateCoreMetadata(error, code);

    internal static string GetStatusName(OmniRelayStatusCode code) => StatusCodeNames[code];

    private static Error AnnotateCoreMetadata(Error error, OmniRelayStatusCode code)
    {
        var updated = error.WithCode(StatusCodeNames[code]).WithMetadata(StatusMetadataKey, code.ToString());

        if (!updated.TryGetMetadata(FaultMetadataKey, out string? _))
        {
            var fault = OmniRelayStatusFacts.GetFaultType(code);
            if (fault != OmniRelayFaultType.Unknown)
            {
                updated = updated.WithMetadata(FaultMetadataKey, fault.ToString());
            }
        }

        if (!updated.TryGetMetadata(RetryableMetadataKey, out bool _))
        {
            updated = updated.WithMetadata(RetryableMetadataKey, OmniRelayStatusFacts.IsRetryable(code));
        }

        return updated;
    }
}
