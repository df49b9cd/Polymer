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
        var hasScheme = trimmed.Contains("://", StringComparison.Ordinal);

        if (hasScheme)
        {
            return TryParseAbsoluteUri(trimmed, out endpoint);
        }

        return TryParseHostPort(trimmed, out endpoint);
    }

    /// <summary>Builds the HTTPS URI used for gossip POST requests.</summary>
    public Uri BuildRequestUri() =>
        new UriBuilder(Uri.UriSchemeHttps, Host, Port, "/mesh/gossip/v1/messages").Uri;

    /// <summary>Builds the HTTPS URI used for shuffle exchanges.</summary>
    public Uri BuildShuffleUri() =>
        new UriBuilder(Uri.UriSchemeHttps, Host, Port, "/mesh/gossip/v1/shuffle").Uri;

    public override string ToString() => $"{Host}:{Port}";

    private static bool TryParseAbsoluteUri(string value, out MeshGossipPeerEndpoint endpoint)
    {
        endpoint = default;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0)
        {
            return false;
        }

        if (!AuthorityIncludesPort(uri.Authority))
        {
            return false;
        }

        endpoint = new MeshGossipPeerEndpoint(uri.Host, uri.Port);
        return true;
    }

    private static bool TryParseHostPort(string value, out MeshGossipPeerEndpoint endpoint)
    {
        endpoint = default;
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1)
        {
            return false;
        }

        var hostPart = value[..colon];
        var portPart = value[(colon + 1)..];

        if (!int.TryParse(portPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort) || parsedPort <= 0)
        {
            return false;
        }

        endpoint = new MeshGossipPeerEndpoint(hostPart, parsedPort);
        return true;
    }

    private static bool AuthorityIncludesPort(string authority)
    {
        if (string.IsNullOrEmpty(authority))
        {
            return false;
        }

        if (authority[0] == '[')
        {
            var closing = authority.IndexOf(']');
            if (closing < 0)
            {
                return false;
            }

            if (closing + 1 >= authority.Length || authority[closing + 1] != ':')
            {
                return false;
            }

            return closing + 2 < authority.Length;
        }

        var colon = authority.LastIndexOf(':');
        return colon > 0 && colon < authority.Length - 1;
    }
}
