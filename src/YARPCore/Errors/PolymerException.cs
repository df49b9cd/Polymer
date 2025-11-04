using Hugo;

namespace YARPCore.Errors;

public sealed class PolymerException : Exception
{
    public PolymerException(
        PolymerStatusCode statusCode,
        string message,
        Error? error = null,
        string? transport = null,
        Exception? innerException = null)
        : base(message, innerException ?? error?.Cause)
    {
        StatusCode = statusCode;
        Error = NormalizeError(error, statusCode, transport) ?? PolymerErrorAdapter.FromStatus(
            statusCode,
            message,
            transport: transport,
            inner: null);
        Transport = transport ?? TryReadTransport(Error);
    }

    public PolymerStatusCode StatusCode { get; }

    public Error Error { get; }

    public string? Transport { get; }

    private static Error? NormalizeError(Error? error, PolymerStatusCode statusCode, string? transport)
    {
        if (error is null)
        {
            return null;
        }

        var normalized = error;

        if (!normalized.TryGetMetadata(PolymerErrorAdapter.StatusMetadataKey, out string? _))
        {
            normalized = PolymerErrorAdapter.WithStatusMetadata(normalized, statusCode);
        }

        if (!string.IsNullOrEmpty(transport))
        {
            normalized = normalized.WithMetadata(PolymerErrorAdapter.TransportMetadataKey, transport);
        }

        var expectedCode = PolymerErrorAdapter.GetStatusName(statusCode);
        if (!string.Equals(normalized.Code, expectedCode, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.WithCode(expectedCode);
        }

        return normalized;
    }

    private static string? TryReadTransport(Error error) =>
        error.TryGetMetadata(PolymerErrorAdapter.TransportMetadataKey, out string? value) ? value : null;
}
