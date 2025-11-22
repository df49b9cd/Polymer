using System.Collections.Immutable;

namespace OmniRelay.Core.Leadership;

/// <summary>
/// Describes a logical leadership scope (global control plane, shard, or custom namespace).
/// Scope identifiers are opaque strings exposed over control-plane APIs and CLI tooling.
/// </summary>
public sealed record LeadershipScope
{
    private static readonly ImmutableDictionary<string, string> EmptyLabels =
        [];

    private LeadershipScope(string scopeId, string scopeKind, ImmutableDictionary<string, string> labels)
    {
        ScopeId = scopeId;
        ScopeKind = scopeKind;
        Labels = labels;
    }

    /// <summary>Canonical identifier for the leadership domain.</summary>
    public string ScopeId { get; }

    /// <summary>Logical classification (global, shard, custom) used for metrics and filtering.</summary>
    public string ScopeKind { get; }

    /// <summary>User-defined labels, typically namespace/shard metadata.</summary>
    public ImmutableDictionary<string, string> Labels { get; }

    /// <summary>Built-in global control scope.</summary>
    public static LeadershipScope GlobalControl { get; } =
        new("global-control", LeadershipScopeKinds.Global, EmptyLabels);

    /// <summary>Create a custom scope with optional labels.</summary>
    public static LeadershipScope Create(string scopeId, string scopeKind, IReadOnlyDictionary<string, string>? labels = null)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
        {
            throw new ArgumentException("Scope identifier cannot be null or whitespace.", nameof(scopeId));
        }

        if (string.IsNullOrWhiteSpace(scopeKind))
        {
            throw new ArgumentException("Scope kind cannot be null or whitespace.", nameof(scopeKind));
        }

        var normalizedLabels = labels is null
            ? EmptyLabels
            : labels.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

        return new LeadershipScope(scopeId.Trim(), scopeKind.Trim().ToLowerInvariant(), normalizedLabels);
    }

    /// <summary>Creates a shard scope using the canonical <c>shard/&lt;namespace&gt;/&lt;shardId&gt;</c> identifier.</summary>
    public static LeadershipScope ForShard(string @namespace, string shardId)
    {
        if (string.IsNullOrWhiteSpace(@namespace))
        {
            throw new ArgumentException("Namespace cannot be null or whitespace.", nameof(@namespace));
        }

        if (string.IsNullOrWhiteSpace(shardId))
        {
            throw new ArgumentException("Shard identifier cannot be null or whitespace.", nameof(shardId));
        }

        var scopeId = $"shard/{@namespace.Trim()}/{shardId.Trim()}";
        var labels = EmptyLabels
            .SetItem("namespace", @namespace.Trim())
            .SetItem("shardId", shardId.Trim());

        return new LeadershipScope(scopeId, LeadershipScopeKinds.Shard, labels);
    }

    /// <summary>Attempts to parse a canonical shard scope identifier.</summary>
    public static bool TryParse(string value, out LeadershipScope scope)
    {
        scope = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, GlobalControl.ScopeId, StringComparison.OrdinalIgnoreCase))
        {
            scope = GlobalControl;
            return true;
        }

        if (trimmed.StartsWith("shard/", StringComparison.OrdinalIgnoreCase))
        {
            var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 3)
            {
                scope = ForShard(segments[1], segments[2]);
                return true;
            }
        }

        scope = Create(trimmed, LeadershipScopeKinds.Custom);
        return true;
    }
}

/// <summary>Well-known scope kinds for metrics and telemetry.</summary>
public static class LeadershipScopeKinds
{
    public const string Global = "global";
    public const string Shard = "shard";
    public const string Custom = "custom";
}

/// <summary>Config-friendly descriptor that is converted into a <see cref="LeadershipScope"/> at runtime.</summary>
public sealed class LeadershipScopeDescriptor
{
    /// <summary>Optional explicit identifier (defaults to canonical form for the provided metadata).</summary>
    public string? ScopeId { get; set; }

    /// <summary>Optional scope kind hint (global/shard/custom). If omitted it is inferred.</summary>
    public string? Kind { get; set; }

    /// <summary>Optional namespace label used to build shard scope identifiers.</summary>
    public string? Namespace { get; set; }

    /// <summary>Optional shard identifier used together with <see cref="Namespace"/>.</summary>
    public string? ShardId { get; set; }

    /// <summary>User supplied labels added verbatim to the resulting scope.</summary>
    public IDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    internal LeadershipScope ToScope()
    {
        if (!string.IsNullOrWhiteSpace(Namespace) && !string.IsNullOrWhiteSpace(ShardId))
        {
            return LeadershipScope.ForShard(Namespace!, ShardId!);
        }

        if (string.IsNullOrWhiteSpace(ScopeId))
        {
            if (string.Equals(Kind, LeadershipScopeKinds.Global, StringComparison.OrdinalIgnoreCase))
            {
                return LeadershipScope.GlobalControl;
            }

            throw new InvalidOperationException("ScopeId must be provided for custom leadership scopes.");
        }

        var inferredKind = string.IsNullOrWhiteSpace(Kind) ? LeadershipScopeKinds.Custom : Kind!.Trim().ToLowerInvariant();
        var normalizedScopeId = ScopeId!.Trim();
        if (string.Equals(normalizedScopeId, LeadershipScope.GlobalControl.ScopeId, StringComparison.OrdinalIgnoreCase))
        {
            return LeadershipScope.GlobalControl;
        }

        var labels = Labels.Count == 0
            ? null
            : Labels.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

        return LeadershipScope.Create(normalizedScopeId, inferredKind, labels);
    }

    internal static LeadershipScopeDescriptor GlobalControl() =>
        new()
        {
            ScopeId = LeadershipScope.GlobalControl.ScopeId,
            Kind = LeadershipScopeKinds.Global
        };
}

/// <summary>Convenience configuration element for bulk-registering shard scopes.</summary>
public sealed class LeadershipShardScopeOptions
{
    public string? Namespace { get; set; }

    public IList<string> Shards { get; } = [];
}
