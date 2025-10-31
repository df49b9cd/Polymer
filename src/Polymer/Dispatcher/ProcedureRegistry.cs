using System;
using System.Collections.Generic;
using System.Linq;

namespace Polymer.Dispatcher;

internal sealed class ProcedureRegistry
{
    private readonly Dictionary<string, ProcedureSpec> _procedures = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public void Register(ProcedureSpec spec)
    {
        if (spec is null)
        {
            throw new ArgumentNullException(nameof(spec));
        }

        var key = CreateKey(spec.Name, spec.Kind);

        lock (_gate)
        {
            if (_procedures.ContainsKey(key))
            {
                throw new InvalidOperationException($"Procedure '{spec.Name}' ({spec.Kind}) is already registered.");
            }

            _procedures.Add(key, spec);
        }
    }

    public bool TryGet(string name, ProcedureKind kind, out ProcedureSpec spec)
    {
        var key = CreateKey(name, kind);

        lock (_gate)
        {
            return _procedures.TryGetValue(key, out spec!);
        }
    }

    public IReadOnlyCollection<ProcedureSpec> Snapshot()
    {
        lock (_gate)
        {
            return _procedures.Values.ToArray();
        }
    }

    private static string CreateKey(string name, ProcedureKind kind) =>
        $"{name}:{kind}";
}
