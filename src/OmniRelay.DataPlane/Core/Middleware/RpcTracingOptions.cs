using System.Diagnostics;

namespace OmniRelay.Core.Middleware;

public sealed class RpcTracingOptions
{
    /// <summary>
    /// Gets or sets the <see cref="ActivitySource"/> used to create spans. When not provided, a shared
    /// <c>ActivitySource</c> named <c>OmniRelay.Rpc</c> is used.
    /// </summary>
    public ActivitySource? ActivitySource { get; init; }

    /// <summary>
    /// Gets or sets the RPC system tag value. Defaults to <c>yarpc</c>.
    /// </summary>
    public string RpcSystem { get; init; } = "yarpc";

    /// <summary>
    /// Gets or sets a value indicating whether inbound middleware should attempt to extract an incoming trace context
    /// from <see cref="RequestMeta.Headers"/>. Enabled by default.
    /// </summary>
    public bool ExtractIncomingContext { get; init; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether outbound middleware should inject the current span context into
    /// <see cref="RequestMeta.Headers"/>. Enabled by default.
    /// </summary>
    public bool InjectOutgoingContext { get; init; } = true;
}
