using System.Collections.Immutable;

namespace OmniRelay.Transport.Http.Middleware;

/// <summary>
/// Fluent builder for configuring HTTP outbound middleware with global, service-level, and per-procedure ordering.
/// </summary>
public sealed class HttpOutboundMiddlewareBuilder
{
    private readonly List<IHttpClientMiddleware> _global = [];
    private readonly Dictionary<string, ServiceBuilder> _services = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Adds a middleware to the global HTTP outbound pipeline.
    /// </summary>
    /// <param name="middleware">The middleware to add.</param>
    public void Use(IHttpClientMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        _global.Add(middleware);
    }

    /// <summary>
    /// Begins configuration for a specific service pipeline.
    /// </summary>
    /// <param name="service">The service name.</param>
    /// <returns>A builder to configure service and per-procedure middleware.</returns>
    public HttpOutboundServiceMiddlewareBuilder ForService(string service)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            throw new ArgumentException("Service name cannot be null or whitespace.", nameof(service));
        }

        if (!_services.TryGetValue(service, out var builder))
        {
            builder = new ServiceBuilder(service);
            _services[service] = builder;
        }

        return new HttpOutboundServiceMiddlewareBuilder(builder);
    }

    internal HttpOutboundMiddlewareRegistry? Build()
    {
        if (_global.Count == 0 && _services.Count == 0)
        {
            return null;
        }

        var global = _global.Count == 0
            ? []
            : ImmutableArray.CreateRange(_global);

        var serviceEntries = ImmutableDictionary.CreateBuilder<string, HttpOutboundMiddlewareRegistry.ServiceEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var (service, builder) in _services)
        {
            serviceEntries[service] = builder.Build(global);
        }

        return new HttpOutboundMiddlewareRegistry(global, serviceEntries.ToImmutable());
    }

    internal sealed class ServiceBuilder(string service)
    {
        // Store and reference the primary constructor parameter to avoid CS9113 (Parameter is unread).
        private readonly string _service = service;
        private readonly List<IHttpClientMiddleware> _serviceMiddleware = [];
        private readonly Dictionary<string, List<IHttpClientMiddleware>> _procedures = new(StringComparer.OrdinalIgnoreCase);

        public void Use(IHttpClientMiddleware middleware)
        {
            ArgumentNullException.ThrowIfNull(middleware);

            _serviceMiddleware.Add(middleware);
        }

        public HttpOutboundProcedureMiddlewareBuilder ForProcedure(string procedure)
        {
            if (string.IsNullOrWhiteSpace(procedure))
            {
                throw new ArgumentException("Procedure name cannot be null or whitespace.", nameof(procedure));
            }

            if (!_procedures.TryGetValue(procedure, out var list))
            {
                list = [];
                _procedures[procedure] = list;
            }

            return new HttpOutboundProcedureMiddlewareBuilder(list);
        }

        public HttpOutboundMiddlewareRegistry.ServiceEntry Build(ImmutableArray<IHttpClientMiddleware> global)
        {
            var service = _serviceMiddleware.Count == 0
                ? []
                : ImmutableArray.CreateRange(_serviceMiddleware);

            var basePipeline = Combine(global, service);

            if (_procedures.Count == 0)
            {
                return new HttpOutboundMiddlewareRegistry.ServiceEntry(basePipeline, []);
            }

            var procedures = ImmutableDictionary.CreateBuilder<string, ImmutableArray<IHttpClientMiddleware>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, middleware) in _procedures)
            {
                var procedureMiddleware = middleware.Count == 0
                    ? []
                    : ImmutableArray.CreateRange(middleware);

                procedures[name] = Combine(basePipeline, procedureMiddleware);
            }

            return new HttpOutboundMiddlewareRegistry.ServiceEntry(basePipeline, procedures.ToImmutable());
        }

        public override string ToString() => $"HttpOutboundService({_service})";
    }

    private static ImmutableArray<IHttpClientMiddleware> Combine(
        ImmutableArray<IHttpClientMiddleware> left,
        ImmutableArray<IHttpClientMiddleware> right)
    {
        if (left.IsDefaultOrEmpty)
        {
            return right;
        }

        if (right.IsDefaultOrEmpty)
        {
            return left;
        }

        var combined = new IHttpClientMiddleware[left.Length + right.Length];
        left.CopyTo(combined);
        right.CopyTo(combined.AsSpan(left.Length));
        return [.. combined];
    }

}

public readonly struct HttpOutboundServiceMiddlewareBuilder : IEquatable<HttpOutboundServiceMiddlewareBuilder>
{
    private readonly HttpOutboundMiddlewareBuilder.ServiceBuilder _builder;

    internal HttpOutboundServiceMiddlewareBuilder(HttpOutboundMiddlewareBuilder.ServiceBuilder builder)
    {
        _builder = builder;
    }

    /// <summary>
    /// Adds middleware to the service-level pipeline.
    /// </summary>
    /// <param name="middleware">The middleware to add.</param>
    /// <returns>The same builder for chaining.</returns>
    public HttpOutboundServiceMiddlewareBuilder Use(IHttpClientMiddleware middleware)
    {
        _builder.Use(middleware);
        return this;
    }

    /// <summary>
    /// Begins configuration for a specific procedure pipeline.
    /// </summary>
    /// <param name="procedure">The procedure name.</param>
    /// <returns>A builder to configure per-procedure middleware.</returns>
    public HttpOutboundProcedureMiddlewareBuilder ForProcedure(string procedure) =>
        _builder.ForProcedure(procedure);

    public override bool Equals(object? obj) => obj is HttpOutboundServiceMiddlewareBuilder other && Equals(other);

    public override int GetHashCode() => _builder?.GetHashCode() ?? 0;

    public static bool operator ==(HttpOutboundServiceMiddlewareBuilder left, HttpOutboundServiceMiddlewareBuilder right) => left.Equals(right);

    public static bool operator !=(HttpOutboundServiceMiddlewareBuilder left, HttpOutboundServiceMiddlewareBuilder right) => !(left == right);

    public bool Equals(HttpOutboundServiceMiddlewareBuilder other) => ReferenceEquals(_builder, other._builder);
}

public readonly struct HttpOutboundProcedureMiddlewareBuilder : IEquatable<HttpOutboundProcedureMiddlewareBuilder>
{
    private readonly List<IHttpClientMiddleware> _middleware;

    internal HttpOutboundProcedureMiddlewareBuilder(List<IHttpClientMiddleware> middleware)
    {
        _middleware = middleware ?? throw new ArgumentNullException(nameof(middleware));
    }

    /// <summary>
    /// Adds middleware to the procedure-level pipeline.
    /// </summary>
    /// <param name="middleware">The middleware to add.</param>
    /// <returns>The same builder for chaining.</returns>
    public HttpOutboundProcedureMiddlewareBuilder Use(IHttpClientMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        _middleware.Add(middleware);
        return this;
    }

    public override bool Equals(object? obj) => obj is HttpOutboundProcedureMiddlewareBuilder other && Equals(other);

    public override int GetHashCode() => _middleware?.GetHashCode() ?? 0;

    public static bool operator ==(HttpOutboundProcedureMiddlewareBuilder left, HttpOutboundProcedureMiddlewareBuilder right) => left.Equals(right);

    public static bool operator !=(HttpOutboundProcedureMiddlewareBuilder left, HttpOutboundProcedureMiddlewareBuilder right) => !(left == right);

    public bool Equals(HttpOutboundProcedureMiddlewareBuilder other) => ReferenceEquals(_middleware, other._middleware);
}
