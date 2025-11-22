using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Peers;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;

namespace OmniRelay.Dispatcher.Config;

internal static class DispatcherConfigMapper
{
    private enum OutboundKind
    {
        Unary,
        Oneway,
        Stream,
        ClientStream,
        Duplex
    }

    public static global::OmniRelay.Dispatcher.Dispatcher CreateDispatcher(
        IServiceProvider services,
        DispatcherComponentRegistry registry,
        DispatcherConfig config,
        Action<IServiceProvider, DispatcherOptions>? configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(config);

        var options = new DispatcherOptions(config.Service);
        options.Mode = ParseMode(config.Mode);

        ApplyInbounds(config.Inbounds, options);
        ApplyOutbounds(services, config.Outbounds, options);
        ApplyMiddleware(services, registry, config.Middleware, options);

        configureOptions?.Invoke(services, options);

        return new global::OmniRelay.Dispatcher.Dispatcher(options);
    }

    private static DeploymentMode ParseMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DeploymentMode.InProc;
        }

        if (Enum.TryParse<DeploymentMode>(value, ignoreCase: true, out var mode))
        {
            return mode;
        }

        throw new InvalidOperationException($"Unsupported deployment mode '{value}'. Valid values: InProc, Sidecar, Edge.");
    }

    private static void ApplyInbounds(InboundsConfig inbounds, DispatcherOptions options)
    {
        foreach (var http in inbounds.Http)
        {
            if (http.Urls.Count == 0)
            {
                continue;
            }

            var inbound = new HttpInbound(
                http.Urls.ToArray(),
                configureServices: null,
                configureApp: null,
                serverRuntimeOptions: null,
                serverTlsOptions: null,
                transportSecurity: null,
                authorizationEvaluator: null);

            options.AddLifecycle(http.Name ?? "http", inbound);
        }

        foreach (var grpc in inbounds.Grpc)
        {
            if (grpc.Urls.Count == 0)
            {
                continue;
            }

            var runtime = new GrpcServerRuntimeOptions
            {
                EnableDetailedErrors = grpc.EnableDetailedErrors
            };

            var inbound = new GrpcInbound(
                grpc.Urls.ToArray(),
                configureServices: null,
                serverRuntimeOptions: runtime,
                serverTlsOptions: null,
                telemetryOptions: null,
                transportSecurity: null,
                authorizationEvaluator: null);

            options.AddLifecycle(grpc.Name ?? "grpc", inbound);
        }
    }

    private static void ApplyOutbounds(IServiceProvider services, OutboundsConfig outbounds, DispatcherOptions options)
    {
        foreach (var (service, set) in outbounds)
        {
            AddOutboundsForShape(service, set.Http.Unary, OutboundKind.Unary, http: true, services, options);
            AddOutboundsForShape(service, set.Http.Oneway, OutboundKind.Oneway, http: true, services, options);

            AddOutboundsForShape(service, set.Grpc.Unary, OutboundKind.Unary, http: false, services, options);
            AddOutboundsForShape(service, set.Grpc.Oneway, OutboundKind.Oneway, http: false, services, options);
            AddOutboundsForShape(service, set.Grpc.Stream, OutboundKind.Stream, http: false, services, options);
            AddOutboundsForShape(service, set.Grpc.ClientStream, OutboundKind.ClientStream, http: false, services, options);
            AddOutboundsForShape(service, set.Grpc.Duplex, OutboundKind.Duplex, http: false, services, options);
        }
    }

    private static void AddOutboundsForShape(
        string service,
        IEnumerable<OutboundTarget> targets,
        OutboundKind kind,
        bool http,
        IServiceProvider services,
        DispatcherOptions options)
    {
        foreach (var target in targets)
        {
            if (target.Addresses.Count == 0)
            {
                continue;
            }

            var addresses = target.Addresses.Select(a => new Uri(a, UriKind.Absolute)).ToArray();

            if (http)
            {
                // only unary/oneway supported for HTTP
                if (kind == OutboundKind.Unary || kind == OutboundKind.Oneway)
                {
                    var client = services.GetService<IHttpClientFactory>()?.CreateClient() ?? new HttpClient();
                    var outbound = new HttpOutbound(client, addresses[0], disposeClient: services.GetService<IHttpClientFactory>() is null);
                    if (kind == OutboundKind.Unary)
                    {
                        options.AddUnaryOutbound(service, target.Key, outbound);
                    }
                    else
                    {
                        options.AddOnewayOutbound(service, target.Key, outbound);
                    }
                }
            }
            else
            {
                var outbound = new GrpcOutbound(
                    addresses,
                    target.RemoteService ?? service,
                    clientTlsOptions: null,
                    peerChooser: peers => new RoundRobinPeerChooser(peers.ToImmutableArray()),
                    clientRuntimeOptions: null,
                    peerCircuitBreakerOptions: null,
                    telemetryOptions: null,
                    endpointHttp3Support: null);

                switch (kind)
                {
                    case OutboundKind.Unary:
                        options.AddUnaryOutbound(service, target.Key, outbound);
                        break;
                    case OutboundKind.Oneway:
                        options.AddOnewayOutbound(service, target.Key, outbound);
                        break;
                    case OutboundKind.Stream:
                        options.AddStreamOutbound(service, target.Key, outbound);
                        break;
                    case OutboundKind.ClientStream:
                        options.AddClientStreamOutbound(service, target.Key, outbound);
                        break;
                    case OutboundKind.Duplex:
                        options.AddDuplexOutbound(service, target.Key, outbound);
                        break;
                }
            }
        }
    }

    private static void ApplyMiddleware(
        IServiceProvider services,
        DispatcherComponentRegistry registry,
        MiddlewareConfig middleware,
        DispatcherOptions options)
    {
        AddMiddlewareStack(services, registry, middleware.Inbound.Unary, ProcedureKind.Unary, inbound: true, options);
        AddMiddlewareStack(services, registry, middleware.Inbound.Oneway, ProcedureKind.Oneway, inbound: true, options);
        AddMiddlewareStack(services, registry, middleware.Inbound.Stream, ProcedureKind.Stream, inbound: true, options);
        AddMiddlewareStack(services, registry, middleware.Inbound.ClientStream, ProcedureKind.ClientStream, inbound: true, options);
        AddMiddlewareStack(services, registry, middleware.Inbound.Duplex, ProcedureKind.Duplex, inbound: true, options);

        AddMiddlewareStack(services, registry, middleware.Outbound.Unary, ProcedureKind.Unary, inbound: false, options);
        AddMiddlewareStack(services, registry, middleware.Outbound.Oneway, ProcedureKind.Oneway, inbound: false, options);
        AddMiddlewareStack(services, registry, middleware.Outbound.Stream, ProcedureKind.Stream, inbound: false, options);
        AddMiddlewareStack(services, registry, middleware.Outbound.ClientStream, ProcedureKind.ClientStream, inbound: false, options);
        AddMiddlewareStack(services, registry, middleware.Outbound.Duplex, ProcedureKind.Duplex, inbound: false, options);
    }

    private static void AddMiddlewareStack(
        IServiceProvider services,
        DispatcherComponentRegistry registry,
        IEnumerable<string> keys,
        ProcedureKind kind,
        bool inbound,
        DispatcherOptions options)
    {
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (inbound)
            {
                if (registry.TryResolveInbound(key, kind, services, out var middleware) && middleware is not null)
                {
                    switch (kind)
                    {
                        case ProcedureKind.Unary:
                            options.UnaryInboundMiddleware.Add((IUnaryInboundMiddleware)middleware);
                            break;
                        case ProcedureKind.Oneway:
                            options.OnewayInboundMiddleware.Add((IOnewayInboundMiddleware)middleware);
                            break;
                        case ProcedureKind.Stream:
                            options.StreamInboundMiddleware.Add((IStreamInboundMiddleware)middleware);
                            break;
                        case ProcedureKind.ClientStream:
                            options.ClientStreamInboundMiddleware.Add((IClientStreamInboundMiddleware)middleware);
                            break;
                        case ProcedureKind.Duplex:
                            options.DuplexInboundMiddleware.Add((IDuplexInboundMiddleware)middleware);
                            break;
                    }
                }
            }
            else
            {
                if (registry.TryResolveOutbound(key, kind, services, out var middleware) && middleware is not null)
                {
                    switch (kind)
                    {
                        case ProcedureKind.Unary:
                            options.UnaryOutboundMiddleware.Add((IUnaryOutboundMiddleware)middleware);
                            break;
                        case ProcedureKind.Oneway:
                            options.OnewayOutboundMiddleware.Add((IOnewayOutboundMiddleware)middleware);
                            break;
                        case ProcedureKind.Stream:
                            options.StreamOutboundMiddleware.Add((IStreamOutboundMiddleware)middleware);
                            break;
                        case ProcedureKind.ClientStream:
                            options.ClientStreamOutboundMiddleware.Add((IClientStreamOutboundMiddleware)middleware);
                            break;
                        case ProcedureKind.Duplex:
                            options.DuplexOutboundMiddleware.Add((IDuplexOutboundMiddleware)middleware);
                            break;
                    }
                }
            }
        }
    }
}

public static class DispatcherConfigServiceCollectionExtensions
{
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "DispatcherConfig is a sealed DTO reachable via source-generated metadata")]
    [RequiresDynamicCode("Configuration binding may emit dynamic code; callers should prefer AddOmniRelayDispatcherFromConfig with source-generated JSON context for AOT.")]
    public static IServiceCollection AddOmniRelayDispatcherFromConfiguration(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<DispatcherComponentRegistry>? registerComponents = null,
        Action<IServiceProvider, DispatcherOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<DispatcherConfig>(configuration);

        services.AddDispatcherComponentRegistry(registry => registerComponents?.Invoke(registry));

        services.AddSingleton(sp =>
        {
            var registry = sp.GetRequiredService<DispatcherComponentRegistry>();
            var dispatcherConfig = sp.GetRequiredService<IOptions<DispatcherConfig>>().Value;
            return DispatcherConfigMapper.CreateDispatcher(sp, registry, dispatcherConfig, configureOptions);
        });

        services.AddSingleton(sp => sp.GetRequiredService<global::OmniRelay.Dispatcher.Dispatcher>().Codecs);

        return services;
    }

    public static IServiceCollection AddOmniRelayDispatcherFromConfig(
        this IServiceCollection services,
        string configPath,
        Action<DispatcherComponentRegistry>? registerComponents = null,
        Action<IServiceProvider, DispatcherOptions>? configureOptions = null)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            throw new ArgumentException("Config path must be provided", nameof(configPath));
        }

        services.AddDispatcherComponentRegistry(registry =>
        {
            registerComponents?.Invoke(registry);
        });

        services.AddSingleton(sp =>
        {
            var registry = sp.GetRequiredService<DispatcherComponentRegistry>();
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize(json, DispatcherConfigJsonContext.Default.DispatcherConfig)
                         ?? throw new InvalidOperationException($"Failed to deserialize dispatcher config from {configPath}.");

            return DispatcherConfigMapper.CreateDispatcher(sp, registry, config, configureOptions);
        });

        services.AddSingleton(sp => sp.GetRequiredService<global::OmniRelay.Dispatcher.Dispatcher>().Codecs);

        return services;
    }
}
