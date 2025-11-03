using System;
using System.Collections.Generic;
using System.Linq;

namespace Polymer.Dispatcher;

internal sealed class ProcedureRegistry
{
    private readonly Dictionary<string, ProcedureSpec> _procedures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WildcardAlias> _wildcardAliases = new();
    private readonly object _gate = new();

    public void Register(ProcedureSpec spec)
    {
        if (spec is null)
        {
            throw new ArgumentNullException(nameof(spec));
        }

        var key = CreateKey(spec.Service, spec.Name, spec.Kind);
        var aliasInfos = spec.Aliases.Select(alias => new AliasInfo(alias, IsWildcard(alias))).ToArray();

        lock (_gate)
        {
            if (_procedures.ContainsKey(key))
            {
                throw new InvalidOperationException($"Procedure '{spec.Name}' ({spec.Kind}) is already registered.");
            }

            if (_aliases.ContainsKey(key))
            {
                throw new InvalidOperationException($"Procedure '{spec.Name}' ({spec.Kind}) conflicts with an existing alias.");
            }

            var aliasSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var alias in aliasInfos)
            {
                if (!aliasSet.Add(alias.Value))
                {
                    throw new InvalidOperationException($"Alias '{alias.Value}' is duplicated for procedure '{spec.Name}'.");
                }

                var aliasKey = CreateKey(spec.Service, alias.Value, spec.Kind);

                if (_procedures.ContainsKey(aliasKey) || _aliases.ContainsKey(aliasKey))
                {
                    throw new InvalidOperationException($"Alias '{alias.Value}' for procedure '{spec.Name}' conflicts with an existing registration.");
                }

                if (_wildcardAliases.Any(entry =>
                        entry.Kind == spec.Kind &&
                        string.Equals(entry.Service, spec.Service, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(entry.Pattern, alias.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"Alias '{alias.Value}' for procedure '{spec.Name}' conflicts with an existing wildcard registration.");
                }
            }

            _procedures.Add(key, spec);

            foreach (var alias in aliasInfos)
            {
                var aliasKey = CreateKey(spec.Service, alias.Value, spec.Kind);
                if (alias.IsWildcard)
                {
                    var specificity = ComputeSpecificity(alias.Value);
                    _wildcardAliases.Add(new WildcardAlias(spec.Service, alias.Value, spec.Kind, key, specificity));
                }
                else
                {
                    _aliases.Add(aliasKey, key);
                }
            }
        }
    }

    public bool TryGet(string service, string name, ProcedureKind kind, out ProcedureSpec spec)
    {
        var key = CreateKey(service, name, kind);

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

            var wildcardMatch = FindWildcardMatch(service, name, kind);
            if (wildcardMatch is not null &&
                _procedures.TryGetValue(wildcardMatch.CanonicalKey, out spec!))
            {
                return true;
            }

            spec = null!;
            return false;
        }
    }

    public IReadOnlyCollection<ProcedureSpec> Snapshot()
    {
        lock (_gate)
        {
            return _procedures.Values.ToArray();
        }
    }

    private static string CreateKey(string service, string name, ProcedureKind kind) =>
        $"{service}::{name}:{kind}";

    private static bool IsWildcard(string alias) => alias.IndexOf('*') >= 0 || alias.IndexOf('?') >= 0;

    private static int ComputeSpecificity(string pattern)
    {
        var count = 0;
        foreach (var ch in pattern)
        {
            if (ch != '*' && ch != '?')
            {
                count++;
            }
        }

        return count;
    }

    private WildcardAlias? FindWildcardMatch(string service, string name, ProcedureKind kind)
    {
        WildcardAlias? best = null;

        foreach (var alias in _wildcardAliases)
        {
            if (!string.Equals(alias.Service, service, StringComparison.OrdinalIgnoreCase) || alias.Kind != kind)
            {
                continue;
            }

            if (!WildcardMatch(alias.Pattern, name))
            {
                continue;
            }

            if (best is null || alias.Specificity > best.Value.Specificity)
            {
                best = alias;
            }
        }

        return best;
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

            if (starIndex != -1)
            {
                p = starIndex + 1;
                match++;
                v = match;
                continue;
            }

            return false;
        }

        while (p < patternSpan.Length && patternSpan[p] == '*')
        {
            p++;
        }

        return p == patternSpan.Length;
    }

    private readonly record struct AliasInfo(string Value, bool IsWildcard);

    private sealed record WildcardAlias(string Service, string Pattern, ProcedureKind Kind, string CanonicalKey, int Specificity);
}
