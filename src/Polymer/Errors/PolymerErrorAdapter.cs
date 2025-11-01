using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Hugo;

namespace Polymer.Errors;

public static class PolymerErrorAdapter
{
    internal const string StatusMetadataKey = "polymer.status";
    internal const string TransportMetadataKey = "polymer.transport";
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
        error = error.WithCode(StatusCodeNames[code]).WithMetadata(StatusMetadataKey, code.ToString());

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
        error.WithCode(StatusCodeNames[code]).WithMetadata(StatusMetadataKey, code.ToString());

    internal static string GetStatusName(PolymerStatusCode code) => StatusCodeNames[code];
}
