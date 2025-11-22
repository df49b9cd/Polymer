using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OmniRelay.Transport.Grpc;

public sealed class GrpcTelemetryOptions
{
    /// <summary>
    /// Enables client-side logging/metrics interceptors for outbound calls. Enabled by default.
    /// </summary>
    public bool EnableClientLogging { get; init; } = true;

    /// <summary>
    /// Enables server-side logging/metrics interceptors for inbound calls. Enabled by default.
    /// </summary>
    public bool EnableServerLogging { get; init; } = true;

    /// <summary>
    /// Provides the <see cref="ILoggerFactory"/> used to materialize client interceptors. When unspecified,
    /// the <see cref="NullLoggerFactory"/> is used.
    /// </summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    internal ILoggerFactory ResolveLoggerFactory() => LoggerFactory ?? NullLoggerFactory.Instance;
}
