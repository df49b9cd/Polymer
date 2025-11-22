using System.Collections.Concurrent;
using System.Collections.Immutable;
using Grpc.Core.Interceptors;

namespace OmniRelay.Transport.Grpc.Interceptors;

/// <summary>
/// Immutable registry that resolves client interceptors by service and procedure.
/// </summary>
public sealed class GrpcClientInterceptorRegistry
{
    private readonly ImmutableArray<Interceptor> _global;
    private readonly ImmutableDictionary<string, ServiceEntry> _services;
    private readonly ConcurrentDictionary<(string Service, string? Procedure), ImmutableArray<Interceptor>> _cache = new();

    internal GrpcClientInterceptorRegistry(
        ImmutableArray<Interceptor> global,
        ImmutableDictionary<string, ServiceEntry> services)
    {
        _global = global;
        _services = services;
    }

    public ImmutableArray<Interceptor> Resolve(string service, string? method)
    {
        var normalizedService = string.IsNullOrWhiteSpace(service) ? string.Empty : service;
        var normalizedProcedure = ExtractProcedure(method);
        return _cache.GetOrAdd((normalizedService, normalizedProcedure), ResolveCore);
    }

    private ImmutableArray<Interceptor> ResolveCore((string Service, string? Procedure) key)
    {
        if (string.IsNullOrEmpty(key.Service) || !_services.TryGetValue(key.Service, out var entry))
        {
            return _global;
        }

        if (key.Procedure is null)
        {
            return entry.DefaultPipeline;
        }

        return entry.ProcedurePipelines.TryGetValue(key.Procedure, out var interceptors)
            ? interceptors
            : entry.DefaultPipeline;
    }

    private static string? ExtractProcedure(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return null;
        }

        var index = method.LastIndexOf('/');
        return index >= 0 && index < method.Length - 1
            ? method[(index + 1)..]
            : method;
    }

    internal sealed record ServiceEntry(
        ImmutableArray<Interceptor> DefaultPipeline,
        ImmutableDictionary<string, ImmutableArray<Interceptor>> ProcedurePipelines)
    {
        public ImmutableArray<Interceptor> DefaultPipeline { get; init; } = DefaultPipeline;

        public ImmutableDictionary<string, ImmutableArray<Interceptor>> ProcedurePipelines { get; init; } = ProcedurePipelines;
    }
}
