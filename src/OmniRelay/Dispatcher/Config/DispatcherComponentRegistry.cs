using Grpc.Core.Interceptors;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Core.Middleware;
using OmniRelay.Transport.Grpc.Interceptors;
using OmniRelay.Transport.Http.Middleware;

namespace OmniRelay.Dispatcher.Config;

/// <summary>Registers known middleware/interceptor instances by key for trim-safe config mapping.</summary>
public sealed class DispatcherComponentRegistry
{
    private readonly Dictionary<string, Func<IServiceProvider, IUnaryInboundMiddleware>> _unaryInbound = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<IServiceProvider, IOnewayInboundMiddleware>> _onewayInbound = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<IServiceProvider, IStreamInboundMiddleware>> _streamInbound = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<IServiceProvider, IClientStreamInboundMiddleware>> _clientStreamInbound = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<IServiceProvider, IDuplexInboundMiddleware>> _duplexInbound = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Func<IServiceProvider, IUnaryOutboundMiddleware>> _unaryOutbound = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<IServiceProvider, IOnewayOutboundMiddleware>> _onewayOutbound = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<IServiceProvider, IStreamOutboundMiddleware>> _streamOutbound = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<IServiceProvider, IClientStreamOutboundMiddleware>> _clientStreamOutbound = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<IServiceProvider, IDuplexOutboundMiddleware>> _duplexOutbound = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Func<IServiceProvider, Interceptor>> _grpcClientInterceptors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<IServiceProvider, Interceptor>> _grpcServerInterceptors = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Func<IServiceProvider, IHttpClientMiddleware>> _httpClientMiddleware = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterInboundMiddleware<T>(string key, Func<IServiceProvider, T> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (typeof(IUnaryInboundMiddleware).IsAssignableFrom(typeof(T)))
        {
            _unaryInbound[key] = sp => (IUnaryInboundMiddleware)factory(sp);
        }

        if (typeof(IOnewayInboundMiddleware).IsAssignableFrom(typeof(T)))
        {
            _onewayInbound[key] = sp => (IOnewayInboundMiddleware)factory(sp);
        }

        if (typeof(IStreamInboundMiddleware).IsAssignableFrom(typeof(T)))
        {
            _streamInbound[key] = sp => (IStreamInboundMiddleware)factory(sp);
        }

        if (typeof(IClientStreamInboundMiddleware).IsAssignableFrom(typeof(T)))
        {
            _clientStreamInbound[key] = sp => (IClientStreamInboundMiddleware)factory(sp);
        }

        if (typeof(IDuplexInboundMiddleware).IsAssignableFrom(typeof(T)))
        {
            _duplexInbound[key] = sp => (IDuplexInboundMiddleware)factory(sp);
        }
    }

    public void RegisterOutboundMiddleware<T>(string key, Func<IServiceProvider, T> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (typeof(IUnaryOutboundMiddleware).IsAssignableFrom(typeof(T)))
        {
            _unaryOutbound[key] = sp => (IUnaryOutboundMiddleware)factory(sp);
        }

        if (typeof(IOnewayOutboundMiddleware).IsAssignableFrom(typeof(T)))
        {
            _onewayOutbound[key] = sp => (IOnewayOutboundMiddleware)factory(sp);
        }

        if (typeof(IStreamOutboundMiddleware).IsAssignableFrom(typeof(T)))
        {
            _streamOutbound[key] = sp => (IStreamOutboundMiddleware)factory(sp);
        }

        if (typeof(IClientStreamOutboundMiddleware).IsAssignableFrom(typeof(T)))
        {
            _clientStreamOutbound[key] = sp => (IClientStreamOutboundMiddleware)factory(sp);
        }

        if (typeof(IDuplexOutboundMiddleware).IsAssignableFrom(typeof(T)))
        {
            _duplexOutbound[key] = sp => (IDuplexOutboundMiddleware)factory(sp);
        }
    }

    public void RegisterHttpClientMiddleware(string key, Func<IServiceProvider, IHttpClientMiddleware> factory)
    {
        _httpClientMiddleware[key] = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public void RegisterGrpcClientInterceptor(string key, Func<IServiceProvider, Interceptor> factory)
    {
        _grpcClientInterceptors[key] = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public void RegisterGrpcServerInterceptor(string key, Func<IServiceProvider, Interceptor> factory)
    {
        _grpcServerInterceptors[key] = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    internal bool TryResolveInbound(string key, ProcedureKind kind, IServiceProvider sp, out object? middleware)
    {
        middleware = kind switch
        {
            ProcedureKind.Unary => _unaryInbound.TryGetValue(key, out var f1) ? f1(sp) : null,
            ProcedureKind.Oneway => _onewayInbound.TryGetValue(key, out var f2) ? f2(sp) : null,
            ProcedureKind.Stream => _streamInbound.TryGetValue(key, out var f3) ? f3(sp) : null,
            ProcedureKind.ClientStream => _clientStreamInbound.TryGetValue(key, out var f4) ? f4(sp) : null,
            ProcedureKind.Duplex => _duplexInbound.TryGetValue(key, out var f5) ? f5(sp) : null,
            _ => null
        };

        return middleware is not null;
    }

    internal bool TryResolveOutbound(string key, ProcedureKind kind, IServiceProvider sp, out object? middleware)
    {
        middleware = kind switch
        {
            ProcedureKind.Unary => _unaryOutbound.TryGetValue(key, out var f1) ? f1(sp) : null,
            ProcedureKind.Oneway => _onewayOutbound.TryGetValue(key, out var f2) ? f2(sp) : null,
            ProcedureKind.Stream => _streamOutbound.TryGetValue(key, out var f3) ? f3(sp) : null,
            ProcedureKind.ClientStream => _clientStreamOutbound.TryGetValue(key, out var f4) ? f4(sp) : null,
            ProcedureKind.Duplex => _duplexOutbound.TryGetValue(key, out var f5) ? f5(sp) : null,
            _ => null
        };

        return middleware is not null;
    }

    internal bool TryResolveHttpClientMiddleware(string key, IServiceProvider sp, out IHttpClientMiddleware? middleware)
    {
        if (_httpClientMiddleware.TryGetValue(key, out var factory))
        {
            middleware = factory(sp);
            return true;
        }

        middleware = null;
        return false;
    }

    internal bool TryResolveGrpcClientInterceptor(string key, IServiceProvider sp, out Interceptor? interceptor)
    {
        if (_grpcClientInterceptors.TryGetValue(key, out var factory))
        {
            interceptor = factory(sp);
            return true;
        }

        interceptor = null;
        return false;
    }

    internal bool TryResolveGrpcServerInterceptor(string key, IServiceProvider sp, out Interceptor? interceptor)
    {
        if (_grpcServerInterceptors.TryGetValue(key, out var factory))
        {
            interceptor = factory(sp);
            return true;
        }

        interceptor = null;
        return false;
    }
}

public static class DispatcherComponentRegistryExtensions
{
    public static IServiceCollection AddDispatcherComponentRegistry(this IServiceCollection services, Action<DispatcherComponentRegistry> register)
    {
        ArgumentNullException.ThrowIfNull(register);
        services.AddSingleton(sp =>
        {
            var registry = new DispatcherComponentRegistry();
            register(registry);
            return registry;
        });
        return services;
    }
}
