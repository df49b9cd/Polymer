using Hugo;
using static Hugo.Go;

namespace OmniRelay.Errors;

/// <summary>
/// Helpers for normalizing exceptions and Hugo <c>Error</c> values to OmniRelay conventions
/// (status code, transport metadata, retryability) and mapping to/from results.
/// </summary>
public static class OmniRelayErrors
{
    /// <summary>
    /// Converts a Hugo <see cref="Error"/> to an <see cref="OmniRelayException"/>, normalizing status and transport.
    /// </summary>
    public static OmniRelayException FromError(Error error, string? transport = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        var status = OmniRelayErrorAdapter.ToStatus(error);
        var normalized = OmniRelayErrorAdapter.WithStatusMetadata(error, status);

        if (!string.IsNullOrEmpty(transport))
        {
            normalized = normalized.WithMetadata(OmniRelayErrorAdapter.TransportMetadataKey, transport);
        }

        return new OmniRelayException(status, normalized.Message, normalized, transport, normalized.Cause);
    }

    /// <summary>
    /// Converts an arbitrary <see cref="Exception"/> to an <see cref="OmniRelayException"/>,
    /// mapping common cases like cancellations and timeouts.
    /// </summary>
    public static OmniRelayException FromException(Exception exception, string? transport = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OmniRelayException omnirelayException)
        {
            return EnsureTransport(omnirelayException, transport);
        }

        if (exception is ResultException resultException && resultException.Error is not null)
        {
            return FromError(resultException.Error, transport);
        }

        if (exception is OperationCanceledException canceled)
        {
            var message = string.IsNullOrEmpty(canceled.Message)
                ? "The operation was cancelled."
                : canceled.Message;

            var error = OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Cancelled,
                message,
                transport,
                inner: Error.Canceled().WithCause(canceled),
                metadata: CreateExceptionMetadata(canceled, "operation-canceled"));

            return new OmniRelayException(OmniRelayStatusCode.Cancelled, message, error, transport, canceled);
        }

        if (exception is TimeoutException timeout)
        {
            var message = string.IsNullOrEmpty(timeout.Message)
                ? "The operation timed out."
                : timeout.Message;

            var error = OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.DeadlineExceeded,
                message,
                transport,
                inner: Error.Timeout().WithCause(timeout),
                metadata: CreateExceptionMetadata(timeout, "operation-timeout"));

            return new OmniRelayException(OmniRelayStatusCode.DeadlineExceeded, message, error, transport, timeout);
        }

        var innerError = Error.FromException(exception)
            .WithMetadata("exceptionType", exception.GetType().FullName);

        var statusError = OmniRelayErrorAdapter.FromStatus(
            OmniRelayStatusCode.Internal,
            string.IsNullOrEmpty(exception.Message) ? "An internal error occurred." : exception.Message,
            transport,
            inner: innerError,
            metadata: CreateExceptionMetadata(exception, "internal-error"));

        return new OmniRelayException(OmniRelayStatusCode.Internal, exception.Message, statusError, transport, exception);
    }

    /// <summary>Checks if the exception represents the given status code.</summary>
    public static bool IsStatus(Exception exception, OmniRelayStatusCode statusCode) =>
        TryGetStatus(exception, out var resolved) && resolved == statusCode;

    /// <summary>Attempts to extract an OmniRelay status code from the exception.</summary>
    public static bool TryGetStatus(Exception exception, out OmniRelayStatusCode statusCode)
    {
        if (exception is OmniRelayException omnirelayException)
        {
            statusCode = omnirelayException.StatusCode;
            return true;
        }

        if (exception is ResultException resultException && resultException.Error is not null)
        {
            statusCode = OmniRelayErrorAdapter.ToStatus(resultException.Error);
            return true;
        }

        if (exception is OperationCanceledException)
        {
            statusCode = OmniRelayStatusCode.Cancelled;
            return true;
        }

        if (exception is TimeoutException)
        {
            statusCode = OmniRelayStatusCode.DeadlineExceeded;
            return true;
        }

        statusCode = OmniRelayStatusCode.Internal;
        return false;
    }

    /// <summary>Classifies the fault type (client/server) for the given exception.</summary>
    public static OmniRelayFaultType GetFaultType(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OmniRelayException omnirelayException)
        {
            return GetFaultType(omnirelayException.Error);
        }

        if (exception is ResultException resultException && resultException.Error is not null)
        {
            return GetFaultType(resultException.Error);
        }

        return TryGetStatus(exception, out var status)
            ? GetFaultType(status)
            : OmniRelayFaultType.Server;
    }

    /// <summary>Classifies the fault type (client/server) for the given status code.</summary>
    public static OmniRelayFaultType GetFaultType(OmniRelayStatusCode statusCode) =>
        OmniRelayStatusFacts.GetFaultType(statusCode);

    /// <summary>Classifies the fault type (client/server) for the given Hugo error.</summary>
    public static OmniRelayFaultType GetFaultType(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error.TryGetMetadata(OmniRelayErrorAdapter.FaultMetadataKey, out string? faultName) &&
            Enum.TryParse<OmniRelayFaultType>(faultName, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return OmniRelayStatusFacts.GetFaultType(OmniRelayErrorAdapter.ToStatus(error));
    }

    /// <summary>Determines whether the given exception is retryable.</summary>
    public static bool IsRetryable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            OmniRelayException omnirelayException => IsRetryable(omnirelayException.Error),
            ResultException resultException when resultException.Error is not null => IsRetryable(resultException.Error),
            _ => TryGetStatus(exception, out var status) && IsRetryable(status)
        };
    }

    /// <summary>Determines whether the given status code is retryable.</summary>
    public static bool IsRetryable(OmniRelayStatusCode statusCode) =>
        OmniRelayStatusFacts.IsRetryable(statusCode);

    /// <summary>Determines whether the given error is retryable.</summary>
    public static bool IsRetryable(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error.TryGetMetadata(OmniRelayErrorAdapter.RetryableMetadataKey, out bool retryable))
        {
            return retryable;
        }

        if (error.TryGetMetadata(OmniRelayErrorAdapter.RetryableMetadataKey, out string? retryableText) &&
            bool.TryParse(retryableText, out var parsed))
        {
            return parsed;
        }

        return OmniRelayStatusFacts.IsRetryable(OmniRelayErrorAdapter.ToStatus(error));
    }

    /// <summary>Wraps an exception as a failed Hugo <c>Result&lt;T&gt;</c> with normalized OmniRelay error.</summary>
    public static Result<T> ToResult<T>(Exception exception, string? transport = null) =>
        Err<T>(FromException(exception, transport).Error);

    /// <summary>Creates a failed result from a status and message.</summary>
    public static Result<T> ToResult<T>(OmniRelayStatusCode statusCode, string message, string? transport = null) =>
        Err<T>(OmniRelayErrorAdapter.FromStatus(statusCode, message, transport));

    /// <summary>Wraps an error as a failed result with normalized OmniRelay exception metadata.</summary>
    public static Result<T> ToResult<T>(Error error, string? transport = null) =>
        Err<T>(FromError(error, transport).Error);

    private static OmniRelayException EnsureTransport(OmniRelayException exception, string? transport)
    {
        if (string.IsNullOrEmpty(transport) ||
            string.Equals(exception.Transport, transport, StringComparison.OrdinalIgnoreCase))
        {
            return exception;
        }

        var updatedError = exception.Error.WithMetadata(OmniRelayErrorAdapter.TransportMetadataKey, transport);
        return new OmniRelayException(exception.StatusCode, exception.Message, updatedError, transport, exception);
    }

    private static Dictionary<string, object?> CreateExceptionMetadata(Exception exception, string stage) =>
        new Dictionary<string, object?>
        {
            ["stage"] = stage,
            ["exceptionType"] = exception.GetType().FullName ?? exception.GetType().Name,
            ["exceptionMessage"] = exception.Message
        };
}
