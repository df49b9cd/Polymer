using Microsoft.Extensions.Configuration;
using YARPCore.Core.Peers;
using YARPCore.Core.Transport;

namespace YARPCore.Configuration;

/// <summary>
/// Defines a custom inbound transport specification that can materialize an inbound transport
/// from configuration.
/// </summary>
public interface ICustomInboundSpec
{
    string Name { get; }

    ILifecycle CreateInbound(IConfigurationSection configuration, IServiceProvider services);
}

/// <summary>
/// Defines a custom outbound transport specification that can materialize outbound transports
/// for the configured RPC kinds.
/// </summary>
public interface ICustomOutboundSpec
{
    string Name { get; }

    IUnaryOutbound? CreateUnaryOutbound(IConfigurationSection configuration, IServiceProvider services) => null;

    IOnewayOutbound? CreateOnewayOutbound(IConfigurationSection configuration, IServiceProvider services) => null;

    IStreamOutbound? CreateStreamOutbound(IConfigurationSection configuration, IServiceProvider services) => null;

    IClientStreamOutbound? CreateClientStreamOutbound(IConfigurationSection configuration, IServiceProvider services) => null;

    IDuplexOutbound? CreateDuplexOutbound(IConfigurationSection configuration, IServiceProvider services) => null;
}

/// <summary>
/// Defines a custom peer chooser specification that can materialize a chooser factory
/// using configuration data.
/// </summary>
public interface ICustomPeerChooserSpec
{
    string Name { get; }

    Func<IReadOnlyList<IPeer>, IPeerChooser> CreateFactory(IConfigurationSection configuration, IServiceProvider services);
}
