using System.Collections.Concurrent;
using System.Collections.Immutable;
using Grpc.Core.Interceptors;

namespace OmniRelay.Transport.Grpc.Interceptors;

/// <summary>
/// Immutable registry that resolves server interceptors per procedure.
/// </summary>
public sealed class GrpcServerInterceptorRegistry
{
    private readonly ImmutableArray<Interceptor> _global;
    private readonly ImmutableDictionary<string, ImmutableArray<Interceptor>> _procedures;
    private readonly ConcurrentDictionary<string, ImmutableArray<Interceptor>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    internal GrpcServerInterceptorRegistry(
        ImmutableArray<Interceptor> global,
        ImmutableDictionary<string, ImmutableArray<Interceptor>> procedures)
    {
        _global = global;
        _procedures = procedures;
    }

    public ImmutableArray<Interceptor> Resolve(string? procedure)
    {
        var normalized = ExtractProcedure(procedure);
        if (normalized is null || _procedures.Count == 0)
        {
            return _global;
        }

        return _cache.GetOrAdd(normalized, ResolveCore);
    }

    private ImmutableArray<Interceptor> ResolveCore(string procedure)
    {
        if (_procedures.TryGetValue(procedure, out var interceptors))
        {
            return interceptors;
        }

        return _global;
    }

    private static string? ExtractProcedure(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return null;
        }

        var index = method.LastIndexOf('/');
        if (index >= 0 && index < method.Length - 1)
        {
            return method[(index + 1)..];
        }

        return method;
    }
}
