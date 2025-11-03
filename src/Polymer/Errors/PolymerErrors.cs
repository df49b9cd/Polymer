using System;
using System.Collections.Generic;
using Hugo;
using static Hugo.Go;

namespace Polymer.Errors;

public static class PolymerErrors
{
    public static PolymerException FromError(Error error, string? transport = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        var status = PolymerErrorAdapter.ToStatus(error);
        var normalized = PolymerErrorAdapter.WithStatusMetadata(error, status);

        if (!string.IsNullOrEmpty(transport))
        {
            normalized = normalized.WithMetadata(PolymerErrorAdapter.TransportMetadataKey, transport);
        }

        return new PolymerException(status, normalized.Message, normalized, transport, normalized.Cause as Exception);
    }

    public static PolymerException FromException(Exception exception, string? transport = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is PolymerException polymerException)
        {
            return EnsureTransport(polymerException, transport);
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

            var error = PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.Cancelled,
                message,
                transport,
                inner: Error.Canceled().WithCause(canceled),
                metadata: CreateExceptionMetadata(canceled, "operation-canceled"));

            return new PolymerException(PolymerStatusCode.Cancelled, message, error, transport, canceled);
        }

        if (exception is TimeoutException timeout)
        {
            var message = string.IsNullOrEmpty(timeout.Message)
                ? "The operation timed out."
                : timeout.Message;

            var error = PolymerErrorAdapter.FromStatus(
                PolymerStatusCode.DeadlineExceeded,
                message,
                transport,
                inner: Error.Timeout().WithCause(timeout),
                metadata: CreateExceptionMetadata(timeout, "operation-timeout"));

            return new PolymerException(PolymerStatusCode.DeadlineExceeded, message, error, transport, timeout);
        }

        var innerError = Error.FromException(exception)
            .WithMetadata("exceptionType", exception.GetType().FullName);

        var statusError = PolymerErrorAdapter.FromStatus(
            PolymerStatusCode.Internal,
            string.IsNullOrEmpty(exception.Message) ? "An internal error occurred." : exception.Message,
            transport,
            inner: innerError,
            metadata: CreateExceptionMetadata(exception, "internal-error"));

        return new PolymerException(PolymerStatusCode.Internal, exception.Message, statusError, transport, exception);
    }

    public static bool IsStatus(Exception exception, PolymerStatusCode statusCode) =>
        TryGetStatus(exception, out var resolved) && resolved == statusCode;

    public static bool TryGetStatus(Exception exception, out PolymerStatusCode statusCode)
    {
        if (exception is PolymerException polymerException)
        {
            statusCode = polymerException.StatusCode;
            return true;
        }

        if (exception is ResultException resultException && resultException.Error is not null)
        {
            statusCode = PolymerErrorAdapter.ToStatus(resultException.Error);
            return true;
        }

        if (exception is OperationCanceledException)
        {
            statusCode = PolymerStatusCode.Cancelled;
            return true;
        }

        if (exception is TimeoutException)
        {
            statusCode = PolymerStatusCode.DeadlineExceeded;
            return true;
        }

        statusCode = PolymerStatusCode.Internal;
        return false;
    }

    public static PolymerFaultType GetFaultType(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is PolymerException polymerException)
        {
            return GetFaultType(polymerException.Error);
        }

        if (exception is ResultException resultException && resultException.Error is not null)
        {
            return GetFaultType(resultException.Error);
        }

        return TryGetStatus(exception, out var status)
            ? GetFaultType(status)
            : PolymerFaultType.Server;
    }

    public static PolymerFaultType GetFaultType(PolymerStatusCode statusCode) =>
        PolymerStatusFacts.GetFaultType(statusCode);

    public static PolymerFaultType GetFaultType(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error.TryGetMetadata(PolymerErrorAdapter.FaultMetadataKey, out string? faultName) &&
            Enum.TryParse<PolymerFaultType>(faultName, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return PolymerStatusFacts.GetFaultType(PolymerErrorAdapter.ToStatus(error));
    }

    public static bool IsRetryable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            PolymerException polymerException => IsRetryable(polymerException.Error),
            ResultException resultException when resultException.Error is not null => IsRetryable(resultException.Error),
            _ => TryGetStatus(exception, out var status) && IsRetryable(status)
        };
    }

    public static bool IsRetryable(PolymerStatusCode statusCode) =>
        PolymerStatusFacts.IsRetryable(statusCode);

    public static bool IsRetryable(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error.TryGetMetadata(PolymerErrorAdapter.RetryableMetadataKey, out bool retryable))
        {
            return retryable;
        }

        if (error.TryGetMetadata(PolymerErrorAdapter.RetryableMetadataKey, out string? retryableText) &&
            bool.TryParse(retryableText, out var parsed))
        {
            return parsed;
        }

        return PolymerStatusFacts.IsRetryable(PolymerErrorAdapter.ToStatus(error));
    }

    public static Result<T> ToResult<T>(Exception exception, string? transport = null) =>
        Err<T>(FromException(exception, transport).Error);

    public static Result<T> ToResult<T>(PolymerStatusCode statusCode, string message, string? transport = null) =>
        Err<T>(PolymerErrorAdapter.FromStatus(statusCode, message, transport));

    public static Result<T> ToResult<T>(Error error, string? transport = null) =>
        Err<T>(FromError(error, transport).Error);

    private static PolymerException EnsureTransport(PolymerException exception, string? transport)
    {
        if (string.IsNullOrEmpty(transport) ||
            string.Equals(exception.Transport, transport, StringComparison.OrdinalIgnoreCase))
        {
            return exception;
        }

        var updatedError = exception.Error.WithMetadata(PolymerErrorAdapter.TransportMetadataKey, transport);
        return new PolymerException(exception.StatusCode, exception.Message, updatedError, transport, exception);
    }

    private static IReadOnlyDictionary<string, object?> CreateExceptionMetadata(Exception exception, string stage) =>
        new Dictionary<string, object?>
        {
            ["stage"] = stage,
            ["exceptionType"] = exception.GetType().FullName ?? exception.GetType().Name,
            ["exceptionMessage"] = exception.Message
        };
}
