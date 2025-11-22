using OmniRelay.Core.Transport;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Helpers to configure tee (shadow) outbounds in dispatcher options.
/// </summary>
public static class DispatcherShadowingExtensions
{
    /// <summary>
    /// Adds a tee unary outbound that forwards to a primary and shadows to a secondary outbound.
    /// </summary>
    public static void AddTeeUnaryOutbound(
        this DispatcherOptions options,
        string service,
        string? key,
        IUnaryOutbound primary,
        IUnaryOutbound shadow,
        TeeOptions? teeOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var outbound = new TeeUnaryOutbound(primary, shadow, teeOptions);
        options.AddUnaryOutbound(service, key, outbound);
    }

    /// <summary>
    /// Adds a tee oneway outbound that forwards to a primary and shadows to a secondary outbound.
    /// </summary>
    public static void AddTeeOnewayOutbound(
        this DispatcherOptions options,
        string service,
        string? key,
        IOnewayOutbound primary,
        IOnewayOutbound shadow,
        TeeOptions? teeOptions = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var outbound = new TeeOnewayOutbound(primary, shadow, teeOptions);
        options.AddOnewayOutbound(service, key, outbound);
    }
}
