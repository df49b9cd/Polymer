using System.Collections.Immutable;
using System.Net;
using System.Security.Authentication;

#pragma warning disable SYSLIB0058
namespace OmniRelay.Transport.Security;

/// <summary>Immutable transport security policy computed from configuration.</summary>
public sealed class TransportSecurityPolicy
{
    public TransportSecurityPolicy(
        bool enabled,
        ImmutableHashSet<string> allowedProtocols,
        ImmutableHashSet<SslProtocols> allowedTlsVersions,
        ImmutableHashSet<CipherAlgorithmType> allowedCipherAlgorithms,
        bool requireClientCertificate,
        ImmutableHashSet<string> allowedThumbprints,
        ImmutableArray<TransportEndpointRule> endpointRules)
    {
        Enabled = enabled;
        AllowedProtocols = allowedProtocols;
        AllowedTlsVersions = allowedTlsVersions;
        AllowedCipherAlgorithms = allowedCipherAlgorithms;
        RequireClientCertificate = requireClientCertificate;
        AllowedThumbprints = allowedThumbprints;
        EndpointRules = endpointRules;
    }

    public bool Enabled { get; }

    public ImmutableHashSet<string> AllowedProtocols { get; }

    public ImmutableHashSet<SslProtocols> AllowedTlsVersions { get; }

    public ImmutableHashSet<CipherAlgorithmType> AllowedCipherAlgorithms { get; }

    public bool RequireClientCertificate { get; }

    public ImmutableHashSet<string> AllowedThumbprints { get; }

    public ImmutableArray<TransportEndpointRule> EndpointRules { get; }
}

/// <summary>Allow/deny rule for hosts or IP ranges.</summary>
public sealed class TransportEndpointRule
{
    public TransportEndpointRule(bool allow, string? hostPattern, IpNetwork? network)
    {
        Allow = allow;
        HostPattern = hostPattern;
        Network = network;
    }

    public bool Allow { get; }

    public string? HostPattern { get; }

    public IpNetwork? Network { get; }

    public bool Matches(IPAddress? address, string? host)
    {
        if (HostPattern is null && Network is null)
        {
            return true;
        }

        if (Network is not null && address is not null && Network.Contains(address))
        {
            return true;
        }

        if (HostPattern is null || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalized = host!.Trim().ToLowerInvariant();
        var pattern = HostPattern.ToLowerInvariant();
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            return normalized.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(pattern, normalized, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Represents an IPv4/IPv6 network in CIDR notation.</summary>
public sealed class IpNetwork
{
    private readonly IPAddress _address;
    private readonly byte[] _maskBytes;

    private IpNetwork(IPAddress address, byte[] maskBytes)
    {
        _address = address;
        _maskBytes = maskBytes;
    }

    public static bool TryParse(string value, out IpNetwork? network)
    {
        network = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address))
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        var totalBits = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > totalBits)
        {
            return false;
        }

        var maskBytes = BuildMask(address, prefixLength);
        network = new IpNetwork(address, maskBytes);
        return true;
    }

    public bool Contains(IPAddress address)
    {
        _ = address.GetAddressBytes();
        var networkBytes = _address.MapToIPv6().GetAddressBytes();
        var candidateBytes = address.MapToIPv6().GetAddressBytes();

        for (var i = 0; i < _maskBytes.Length; i++)
        {
            var mask = _maskBytes[i];
            if ((networkBytes[i] & mask) != (candidateBytes[i] & mask))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] BuildMask(IPAddress address, int prefixLength)
    {
        var bytes = address.MapToIPv6().GetAddressBytes();
        var mask = new byte[bytes.Length];
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        for (var i = 0; i < mask.Length; i++)
        {
            if (i < fullBytes)
            {
                mask[i] = 0xFF;
            }
            else if (i == fullBytes && remainingBits > 0)
            {
                mask[i] = (byte)(byte.MaxValue << (8 - remainingBits));
            }
            else
            {
                mask[i] = 0;
            }
        }

        return mask;
    }
}
#pragma warning restore SYSLIB0058
