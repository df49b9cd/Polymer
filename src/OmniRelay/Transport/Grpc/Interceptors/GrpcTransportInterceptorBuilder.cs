using System.Collections.Immutable;
using Grpc.Core.Interceptors;

namespace OmniRelay.Transport.Grpc.Interceptors;

/// <summary>
/// Builder that configures gRPC interceptors with global, service-level, and per-procedure ordering.
/// </summary>
public sealed class GrpcTransportInterceptorBuilder
{
    private readonly List<Interceptor> _globalClient = [];
    private readonly Dictionary<string, ClientServiceBuilder> _clientServices = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Interceptor> _globalServer = [];
    private readonly Dictionary<string, List<Interceptor>> _serverProcedures = new(StringComparer.OrdinalIgnoreCase);

    public void UseClient(Interceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);

        _globalClient.Add(interceptor);
    }

    public GrpcClientServiceInterceptorBuilder ForClientService(string service)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            throw new ArgumentException("Service name cannot be null or whitespace.", nameof(service));
        }

        if (!_clientServices.TryGetValue(service, out var builder))
        {
            builder = new ClientServiceBuilder(service);
            _clientServices[service] = builder;
        }

        return new GrpcClientServiceInterceptorBuilder(builder);
    }

    public void UseServer(Interceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);

        _globalServer.Add(interceptor);
    }

    public GrpcServerProcedureInterceptorBuilder ForServerProcedure(string procedure)
    {
        if (string.IsNullOrWhiteSpace(procedure))
        {
            throw new ArgumentException("Procedure name cannot be null or whitespace.", nameof(procedure));
        }

        if (!_serverProcedures.TryGetValue(procedure, out var list))
        {
            list = [];
            _serverProcedures[procedure] = list;
        }

        return new GrpcServerProcedureInterceptorBuilder(list);
    }

    internal GrpcClientInterceptorRegistry? BuildClientRegistry()
    {
        if (_globalClient.Count == 0 && _clientServices.Count == 0)
        {
            return null;
        }

        var global = ImmutableArray.CreateRange(_globalClient);
        var services = ImmutableDictionary.CreateBuilder<string, GrpcClientInterceptorRegistry.ServiceEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var (service, builder) in _clientServices)
        {
            services[service] = builder.Build(global);
        }

        return new GrpcClientInterceptorRegistry(global, services.ToImmutable());
    }

    internal GrpcServerInterceptorRegistry? BuildServerRegistry()
    {
        if (_globalServer.Count == 0 && _serverProcedures.Count == 0)
        {
            return null;
        }

        var global = ImmutableArray.CreateRange(_globalServer);
        if (_serverProcedures.Count == 0)
        {
            return new GrpcServerInterceptorRegistry(global, []);
        }

        var procedures = ImmutableDictionary.CreateBuilder<string, ImmutableArray<Interceptor>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (procedure, list) in _serverProcedures)
        {
            var pipeline = list.Count == 0
                ? []
                : ImmutableArray.CreateRange(list);

            procedures[procedure] = Combine(global, pipeline);
        }

        return new GrpcServerInterceptorRegistry(global, procedures.ToImmutable());
    }

    private static ImmutableArray<Interceptor> Combine(
        ImmutableArray<Interceptor> left,
        ImmutableArray<Interceptor> right)
    {
        if (left.IsDefaultOrEmpty)
        {
            return right;
        }

        if (right.IsDefaultOrEmpty)
        {
            return left;
        }

        var combined = new Interceptor[left.Length + right.Length];
        left.CopyTo(combined);
        right.CopyTo(combined.AsSpan(left.Length));
        return [.. combined];
    }

    internal sealed class ClientServiceBuilder(string service)
    {
        // Store and reference the primary constructor parameter to avoid CS9113 (Parameter is unread).
        private readonly string _service = service;
        private readonly List<Interceptor> _serviceInterceptors = [];
        private readonly Dictionary<string, List<Interceptor>> _procedures = new(StringComparer.OrdinalIgnoreCase);

        public void Use(Interceptor interceptor)
        {
            ArgumentNullException.ThrowIfNull(interceptor);

            _serviceInterceptors.Add(interceptor);
        }

        public GrpcClientProcedureInterceptorBuilder ForProcedure(string procedure)
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

            return new GrpcClientProcedureInterceptorBuilder(list);
        }

        public GrpcClientInterceptorRegistry.ServiceEntry Build(ImmutableArray<Interceptor> global)
        {
            var service = _serviceInterceptors.Count == 0
                ? []
                : ImmutableArray.CreateRange(_serviceInterceptors);

            var basePipeline = Combine(global, service);

            if (_procedures.Count == 0)
            {
                return new GrpcClientInterceptorRegistry.ServiceEntry(basePipeline, []);
            }

            var procedures = ImmutableDictionary.CreateBuilder<string, ImmutableArray<Interceptor>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (procedure, list) in _procedures)
            {
                var procedureInterceptors = list.Count == 0
                    ? []
                    : ImmutableArray.CreateRange(list);

                procedures[procedure] = Combine(basePipeline, procedureInterceptors);
            }

            return new GrpcClientInterceptorRegistry.ServiceEntry(basePipeline, procedures.ToImmutable());
        }

        public override string ToString() => $"GrpcClientService({_service})";
    }
}

public readonly struct GrpcClientServiceInterceptorBuilder : IEquatable<GrpcClientServiceInterceptorBuilder>
{
    private readonly GrpcTransportInterceptorBuilder.ClientServiceBuilder _builder;

    internal GrpcClientServiceInterceptorBuilder(GrpcTransportInterceptorBuilder.ClientServiceBuilder builder)
    {
        _builder = builder;
    }

    public GrpcClientServiceInterceptorBuilder Use(Interceptor interceptor)
    {
        _builder.Use(interceptor);
        return this;
    }

    public GrpcClientProcedureInterceptorBuilder ForProcedure(string procedure) =>
        _builder.ForProcedure(procedure);

    public override bool Equals(object obj)
    {
        throw new NotImplementedException();
    }

    public override int GetHashCode()
    {
        throw new NotImplementedException();
    }

    public static bool operator ==(GrpcClientServiceInterceptorBuilder left, GrpcClientServiceInterceptorBuilder right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(GrpcClientServiceInterceptorBuilder left, GrpcClientServiceInterceptorBuilder right)
    {
        return !(left == right);
    }

    public bool Equals(GrpcClientServiceInterceptorBuilder other)
    {
        throw new NotImplementedException();
    }
}

public readonly struct GrpcClientProcedureInterceptorBuilder : IEquatable<GrpcClientProcedureInterceptorBuilder>
{
    private readonly List<Interceptor> _interceptors;

    internal GrpcClientProcedureInterceptorBuilder(List<Interceptor> interceptors)
    {
        _interceptors = interceptors ?? throw new ArgumentNullException(nameof(interceptors));
    }

    public GrpcClientProcedureInterceptorBuilder Use(Interceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);

        _interceptors.Add(interceptor);
        return this;
    }

    public override bool Equals(object obj)
    {
        throw new NotImplementedException();
    }

    public override int GetHashCode()
    {
        throw new NotImplementedException();
    }

    public static bool operator ==(GrpcClientProcedureInterceptorBuilder left, GrpcClientProcedureInterceptorBuilder right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(GrpcClientProcedureInterceptorBuilder left, GrpcClientProcedureInterceptorBuilder right)
    {
        return !(left == right);
    }

    public bool Equals(GrpcClientProcedureInterceptorBuilder other)
    {
        throw new NotImplementedException();
    }
}

public readonly struct GrpcServerProcedureInterceptorBuilder : IEquatable<GrpcServerProcedureInterceptorBuilder>
{
    private readonly List<Interceptor> _interceptors;

    internal GrpcServerProcedureInterceptorBuilder(List<Interceptor> interceptors)
    {
        _interceptors = interceptors ?? throw new ArgumentNullException(nameof(interceptors));
    }

    public GrpcServerProcedureInterceptorBuilder Use(Interceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);

        _interceptors.Add(interceptor);
        return this;
    }

    public override bool Equals(object obj)
    {
        throw new NotImplementedException();
    }

    public override int GetHashCode()
    {
        throw new NotImplementedException();
    }

    public static bool operator ==(GrpcServerProcedureInterceptorBuilder left, GrpcServerProcedureInterceptorBuilder right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(GrpcServerProcedureInterceptorBuilder left, GrpcServerProcedureInterceptorBuilder right)
    {
        return !(left == right);
    }

    public bool Equals(GrpcServerProcedureInterceptorBuilder other)
    {
        throw new NotImplementedException();
    }
}
