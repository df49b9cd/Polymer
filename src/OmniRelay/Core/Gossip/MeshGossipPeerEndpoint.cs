using System.Globalization;

namespace OmniRelay.Core.Gossip;

/// <summary>Represents a gossip endpoint (host + port).</summary>
public readonly record struct MeshGossipPeerEndpoint(string Host, int Port)
{
    public static bool TryParse(string value, out MeshGossipPeerEndpoint endpoint)
    {
        endpoint = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            var port = uri.Port > 0 ? uri.Port : 0;
            if (port == 0)
            {
                return false;
            }

            endpoint = new MeshGossipPeerEndpoint(host, port);
            return true;
        }

        var colon = trimmed.LastIndexOf(':');
        if (colon <= 0 || colon == trimmed.Length - 1)
        {
            return false;
        }

        var hostPart = trimmed[..colon];
        var portPart = trimmed[(colon + 1)..];

        if (!int.TryParse(portPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort) || parsedPort <= 0)
        {
            return false;
        }

        endpoint = new MeshGossipPeerEndpoint(hostPart, parsedPort);
        return true;
    }

    /// <summary>Builds the HTTPS URI used for gossip POST requests.</summary>
    public Uri BuildRequestUri() =>
        new UriBuilder(Uri.UriSchemeHttps, Host, Port, "/mesh/gossip/v1/messages").Uri;

    public override string ToString() => $"{Host}:{Port}";
}
