using Hugo;

namespace OmniRelay.Errors;

/// <summary>
/// Exception type representing normalized OmniRelay errors with status code and transport metadata.
/// </summary>
public sealed class OmniRelayException : Exception
{
    /// <summary>
    /// Creates a new OmniRelay exception with a status, message, and optional underlying error and transport.
    /// </summary>
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

    /// <summary>Gets the normalized status code.</summary>
    public OmniRelayStatusCode StatusCode { get; }

    /// <summary>Gets the normalized Hugo <c>Error</c> value.</summary>
    public Error Error { get; }

    /// <summary>Gets the transport name, if known.</summary>
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

    public OmniRelayException()
    {
    }

    public OmniRelayException(string message) : base(message)
    {
    }

    public OmniRelayException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
