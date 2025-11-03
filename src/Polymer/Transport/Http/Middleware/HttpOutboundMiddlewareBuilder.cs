using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Polymer.Transport.Http.Middleware;

/// <summary>
/// Fluent builder for configuring HTTP outbound middleware with global, service-level, and per-procedure ordering.
/// </summary>
public sealed class HttpOutboundMiddlewareBuilder
{
    private readonly List<IHttpClientMiddleware> _global = [];
    private readonly Dictionary<string, ServiceBuilder> _services = new(StringComparer.OrdinalIgnoreCase);

    public void Use(IHttpClientMiddleware middleware)
    {
        if (middleware is null)
        {
            throw new ArgumentNullException(nameof(middleware));
        }

        _global.Add(middleware);
    }

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

    internal sealed class ServiceBuilder
    {
        private readonly string _service;
        private readonly List<IHttpClientMiddleware> _serviceMiddleware = [];
        private readonly Dictionary<string, List<IHttpClientMiddleware>> _procedures = new(StringComparer.OrdinalIgnoreCase);

        public ServiceBuilder(string service)
        {
            _service = service;
        }

        public void Use(IHttpClientMiddleware middleware)
        {
            if (middleware is null)
            {
                throw new ArgumentNullException(nameof(middleware));
            }

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
        return ImmutableArray.Create(combined);
    }

}

public readonly struct HttpOutboundServiceMiddlewareBuilder
{
    private readonly HttpOutboundMiddlewareBuilder.ServiceBuilder _builder;

    internal HttpOutboundServiceMiddlewareBuilder(HttpOutboundMiddlewareBuilder.ServiceBuilder builder)
    {
        _builder = builder;
    }

    public HttpOutboundServiceMiddlewareBuilder Use(IHttpClientMiddleware middleware)
    {
        _builder.Use(middleware);
        return this;
    }

    public HttpOutboundProcedureMiddlewareBuilder ForProcedure(string procedure) =>
        _builder.ForProcedure(procedure);
}

public readonly struct HttpOutboundProcedureMiddlewareBuilder
{
    private readonly List<IHttpClientMiddleware> _middleware;

    internal HttpOutboundProcedureMiddlewareBuilder(List<IHttpClientMiddleware> middleware)
    {
        _middleware = middleware ?? throw new ArgumentNullException(nameof(middleware));
    }

    public HttpOutboundProcedureMiddlewareBuilder Use(IHttpClientMiddleware middleware)
    {
        if (middleware is null)
        {
            throw new ArgumentNullException(nameof(middleware));
        }

        _middleware.Add(middleware);
        return this;
    }
}
