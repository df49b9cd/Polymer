using System;
using Polymer.Core.Transport;

namespace Polymer.Dispatcher;

public static class DispatcherShadowingExtensions
{
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
