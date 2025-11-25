using System.Collections.Immutable;

namespace OmniRelay.Security.Authorization;

/// <summary>Represents a set of role/cluster/principal requirements.</summary>
public sealed class MeshAuthorizationPolicy
{
    public MeshAuthorizationPolicy(
        string name,
        string transport,
        string? pathPrefix,
        ImmutableHashSet<string> allowedRoles,
        ImmutableHashSet<string> allowedClusters,
        ImmutableDictionary<string, string> requiredLabels,
        ImmutableHashSet<string> principals,
        bool requireMutualTls)
    {
        Name = name;
        Transport = transport;
        PathPrefix = pathPrefix;
        AllowedRoles = allowedRoles;
        AllowedClusters = allowedClusters;
        RequiredLabels = requiredLabels;
        Principals = principals;
        RequireMutualTls = requireMutualTls;
    }

    public string Name { get; }

    public string Transport { get; }

    public string? PathPrefix { get; }

    public ImmutableHashSet<string> AllowedRoles { get; }

    public ImmutableHashSet<string> AllowedClusters { get; }

    public ImmutableDictionary<string, string> RequiredLabels { get; }

    public ImmutableHashSet<string> Principals { get; }

    public bool RequireMutualTls { get; }

    public bool Matches(string transport, string endpoint)
    {
        if (!string.Equals(Transport, transport, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Transport, "*", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(PathPrefix))
        {
            return true;
        }

        return endpoint.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
