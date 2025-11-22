namespace OmniRelay.Dispatcher;

/// <summary>
/// Thread-safe registry for procedures and their alias mappings (including wildcard aliases).
/// </summary>
internal sealed class ProcedureRegistry
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    private readonly Dictionary<ProcedureKey, ProcedureSpec> _procedures = new();
    private readonly Dictionary<ProcedureKey, ProcedureKey> _aliases = new();
    private readonly Dictionary<ServiceKindKey, List<WildcardAlias>> _wildcardBuckets = new();
    private readonly Lock _gate = new();

    /// <summary>
    /// Registers a procedure and its aliases, validating conflicts and wildcard patterns.
    /// </summary>
    public void Register(ProcedureSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var canonicalKey = new ProcedureKey(spec.Service, spec.Name, spec.Kind);
        var aliasInfos = BuildAliasInfo(spec.Aliases);

        lock (_gate)
        {
            if (_procedures.ContainsKey(canonicalKey))
            {
                throw new InvalidOperationException($"Procedure '{spec.Name}' ({spec.Kind}) is already registered.");
            }

            if (_aliases.ContainsKey(canonicalKey))
            {
                throw new InvalidOperationException($"Procedure '{spec.Name}' ({spec.Kind}) conflicts with an existing alias.");
            }

            var aliasSet = new HashSet<string>(Comparer);
            foreach (var alias in aliasInfos)
            {
                if (!aliasSet.Add(alias.Value))
                {
                    throw new InvalidOperationException($"Alias '{alias.Value}' is duplicated for procedure '{spec.Name}'.");
                }

                var aliasKey = new ProcedureKey(spec.Service, alias.Value, spec.Kind);

                if (_procedures.ContainsKey(aliasKey) || _aliases.ContainsKey(aliasKey))
                {
                    throw new InvalidOperationException($"Alias '{alias.Value}' for procedure '{spec.Name}' conflicts with an existing registration.");
                }

                if (TryGetWildcardBucket(spec.Service, spec.Kind, out var bucket) &&
                    ContainsWildcardPattern(bucket, alias.Value))
                {
                    throw new InvalidOperationException($"Alias '{alias.Value}' for procedure '{spec.Name}' conflicts with an existing wildcard registration.");
                }
            }

            _procedures.Add(canonicalKey, spec);

            foreach (var alias in aliasInfos)
            {
                var aliasKey = new ProcedureKey(spec.Service, alias.Value, spec.Kind);
                if (alias.IsWildcard)
                {
                    var bucket = GetOrCreateWildcardBucket(new ServiceKindKey(spec.Service, spec.Kind));
                    InsertWildcardSorted(bucket, new WildcardAlias(alias.Value, canonicalKey, ComputeSpecificity(alias.Value)));
                }
                else
                {
                    _aliases.Add(aliasKey, canonicalKey);
                }
            }
        }
    }

    /// <summary>
    /// Attempts to resolve a procedure by name and kind, evaluating direct and wildcard aliases.
    /// </summary>
    public bool TryGet(string service, string name, ProcedureKind kind, out ProcedureSpec spec)
    {
        var key = new ProcedureKey(service, name, kind);

        lock (_gate)
        {
            if (_procedures.TryGetValue(key, out spec!))
            {
                return true;
            }

            if (_aliases.TryGetValue(key, out var canonical) &&
                _procedures.TryGetValue(canonical, out spec!))
            {
                return true;
            }

            if (TryGetWildcardBucket(service, kind, out var bucket))
            {
                for (var i = 0; i < bucket.Count; i++)
                {
                    var alias = bucket[i];
                    if (WildcardMatch(alias.Pattern, name) &&
                        _procedures.TryGetValue(alias.CanonicalKey, out spec!))
                    {
                        return true;
                    }
                }
            }

            spec = null!;
            return false;
        }
    }

    /// <summary>
    /// Returns a snapshot of all registered procedures.
    /// </summary>
    public IReadOnlyCollection<ProcedureSpec> Snapshot()
    {
        lock (_gate)
        {
            return _procedures.Count == 0
                ? Array.Empty<ProcedureSpec>()
                : [.. _procedures.Values];
        }
    }

    private static AliasInfo[] BuildAliasInfo(IReadOnlyList<string> aliases)
    {
        if (aliases.Count == 0)
        {
            return Array.Empty<AliasInfo>();
        }

        var buffer = new AliasInfo[aliases.Count];
        for (var i = 0; i < aliases.Count; i++)
        {
            var alias = aliases[i];
            buffer[i] = new AliasInfo(alias, IsWildcard(alias));
        }

        return buffer;
    }

    private bool TryGetWildcardBucket(string service, ProcedureKind kind, out List<WildcardAlias> bucket) =>
        _wildcardBuckets.TryGetValue(new ServiceKindKey(service, kind), out bucket!);

    private List<WildcardAlias> GetOrCreateWildcardBucket(ServiceKindKey key)
    {
        if (_wildcardBuckets.TryGetValue(key, out var bucket))
        {
            return bucket;
        }

        bucket = [];
        _wildcardBuckets[key] = bucket;
        return bucket;
    }

    private static bool ContainsWildcardPattern(List<WildcardAlias> bucket, string alias)
    {
        for (var i = 0; i < bucket.Count; i++)
        {
            if (Comparer.Equals(bucket[i].Pattern, alias))
            {
                return true;
            }
        }

        return false;
    }

    private static void InsertWildcardSorted(List<WildcardAlias> bucket, WildcardAlias alias)
    {
        var index = 0;

        while (index < bucket.Count && bucket[index].Specificity >= alias.Specificity)
        {
            index++;
        }

        bucket.Insert(index, alias);
    }

    private static bool IsWildcard(string alias) => alias.AsSpan().IndexOfAny('*', '?') >= 0;

    private static int ComputeSpecificity(string pattern)
    {
        var count = 0;

        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch != '*' && ch != '?')
            {
                count++;
            }
        }

        return count;
    }

    private static bool WildcardMatch(string pattern, string value)
    {
        var patternSpan = pattern.AsSpan();
        var valueSpan = value.AsSpan();
        int p = 0, v = 0;
        int starIndex = -1, match = 0;

        while (v < valueSpan.Length)
        {
            var patternChar = p < patternSpan.Length ? char.ToLowerInvariant(patternSpan[p]) : char.MinValue;
            var valueChar = char.ToLowerInvariant(valueSpan[v]);

            if (p < patternSpan.Length && (patternChar == valueChar || patternChar == '?'))
            {
                p++;
                v++;
                continue;
            }

            if (p < patternSpan.Length && patternSpan[p] == '*')
            {
                starIndex = p++;
                match = v;
                continue;
            }

            if (starIndex == -1)
            {
                return false;
            }

            p = starIndex + 1;
            match++;
            v = match;
        }

        while (p < patternSpan.Length && patternSpan[p] == '*')
        {
            p++;
        }

        return p == patternSpan.Length;
    }

    private readonly record struct AliasInfo(string Value, bool IsWildcard);

    private readonly struct ServiceKindKey : IEquatable<ServiceKindKey>
    {
        public ServiceKindKey(string service, ProcedureKind kind)
        {
            Service = service ?? string.Empty;
            Kind = kind;
        }

        public string Service { get; }

        public ProcedureKind Kind { get; }

        public bool Equals(ServiceKindKey other) =>
            Comparer.Equals(Service, other.Service) && Kind == other.Kind;

        public override bool Equals(object? obj) => obj is ServiceKindKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Service, Comparer);
            hash.Add(Kind);
            return hash.ToHashCode();
        }
    }

    private readonly struct ProcedureKey : IEquatable<ProcedureKey>
    {
        public ProcedureKey(string service, string name, ProcedureKind kind)
        {
            Service = service ?? string.Empty;
            Name = name ?? string.Empty;
            Kind = kind;
        }

        public string Service { get; }

        public string Name { get; }

        public ProcedureKind Kind { get; }

        public bool Equals(ProcedureKey other) =>
            Comparer.Equals(Service, other.Service) &&
            Comparer.Equals(Name, other.Name) &&
            Kind == other.Kind;

        public override bool Equals(object? obj) => obj is ProcedureKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Service, Comparer);
            hash.Add(Name, Comparer);
            hash.Add(Kind);
            return hash.ToHashCode();
        }
    }

    private readonly record struct WildcardAlias(string Pattern, ProcedureKey CanonicalKey, int Specificity);
}
