using Hugo;

namespace OmniRelay.Errors;

public sealed class OmniRelayException : Exception
{
    public OmniRelayException(
        OmniRelayStatusCode statusCode,
        string message,
        Error? error = null,
        string? transport = null,
        Exception? innerException = null)
        : base(message, innerException ?? error?.Cause)
    {
        StatusCode = statusCode;
        Error = NormalizeError(error, statusCode, transport) ?? OmniRelayErrorAdapter.FromStatus(
            statusCode,
            message,
            transport: transport,
            inner: null);
        Transport = transport ?? TryReadTransport(Error);
    }

    public OmniRelayStatusCode StatusCode { get; }

    public Error Error { get; }

    public string? Transport { get; }

    private static Error? NormalizeError(Error? error, OmniRelayStatusCode statusCode, string? transport)
    {
        if (error is null)
        {
            return null;
        }

        var normalized = error;

        if (!normalized.TryGetMetadata(OmniRelayErrorAdapter.StatusMetadataKey, out string? _))
        {
            normalized = OmniRelayErrorAdapter.WithStatusMetadata(normalized, statusCode);
        }

        if (!string.IsNullOrEmpty(transport))
        {
            normalized = normalized.WithMetadata(OmniRelayErrorAdapter.TransportMetadataKey, transport);
        }

        var expectedCode = OmniRelayErrorAdapter.GetStatusName(statusCode);
        if (!string.Equals(normalized.Code, expectedCode, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.WithCode(expectedCode);
        }

        return normalized;
    }

    private static string? TryReadTransport(Error error) =>
        error.TryGetMetadata(OmniRelayErrorAdapter.TransportMetadataKey, out string? value) ? value : null;
}
