using System.Diagnostics.Metrics;

namespace OmniRelay.Core.Middleware;

/// <summary>
/// Options for RPC metrics middleware, including meter and metric name prefix.
/// </summary>
public sealed class RpcMetricsOptions
{
    /// <summary>
    /// Gets or sets the <see cref="Meter"/> used by the middleware. When unspecified, a shared meter named
    /// <c>OmniRelay.Rpc</c> is used.
    /// </summary>
    public Meter? Meter { get; init; }

    /// <summary>
    /// Gets or sets the base metric name prefix. Defaults to <c>yarpcore.rpc</c>.
    /// </summary>
    public string MetricPrefix { get; init; } = "yarpcore.rpc";
}
