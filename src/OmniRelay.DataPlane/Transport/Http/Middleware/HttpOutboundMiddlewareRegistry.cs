using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace OmniRelay.Transport.Http.Middleware;

/// <summary>
/// Immutable registry containing HTTP outbound middleware resolved by service and procedure.
/// </summary>
public sealed class HttpOutboundMiddlewareRegistry
{
    private readonly ImmutableArray<IHttpClientMiddleware> _global;
    private readonly ImmutableDictionary<string, ServiceEntry> _services;
    private readonly ConcurrentDictionary<(string Service, string? Procedure), ImmutableArray<IHttpClientMiddleware>> _cache = new();

    internal HttpOutboundMiddlewareRegistry(
        ImmutableArray<IHttpClientMiddleware> global,
        ImmutableDictionary<string, ServiceEntry> services)
    {
        _global = global;
        _services = services;
    }

    public IReadOnlyList<IHttpClientMiddleware> Resolve(string service, string? procedure)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            return _global;
        }

        var key = (service, NormalizeProcedure(procedure));
        return _cache.GetOrAdd(key, ResolveCore);
    }

    private ImmutableArray<IHttpClientMiddleware> ResolveCore((string Service, string? Procedure) key)
    {
        if (!_services.TryGetValue(key.Service, out var entry))
        {
            return _global;
        }

        if (key.Procedure is null)
        {
            return entry.DefaultPipeline;
        }

        if (entry.ProcedurePipelines.TryGetValue(key.Procedure, out var pipeline))
        {
            return pipeline;
        }

        return entry.DefaultPipeline;
    }

    private static string? NormalizeProcedure(string? procedure) =>
        string.IsNullOrWhiteSpace(procedure) ? null : procedure;

    internal sealed record ServiceEntry(
        ImmutableArray<IHttpClientMiddleware> DefaultPipeline,
        ImmutableDictionary<string, ImmutableArray<IHttpClientMiddleware>> ProcedurePipelines);
}
