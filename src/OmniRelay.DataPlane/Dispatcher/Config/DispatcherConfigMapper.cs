using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Binder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Peers;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;

namespace OmniRelay.Dispatcher.Config;

internal static partial class DispatcherConfigMapper
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
            var addresses = target.Addresses;
            if ((addresses is null || addresses.Count == 0) && !string.IsNullOrWhiteSpace(target.Url))
            {
                addresses = new List<string> { target.Url };
            }

            if (addresses is null || addresses.Count == 0)
            {
                continue;
            }

            var uriAddresses = addresses.Select(a => new Uri(a, UriKind.Absolute)).ToArray();

            if (http)
            {
                // only unary/oneway supported for HTTP
                if (kind == OutboundKind.Unary || kind == OutboundKind.Oneway)
                {
                    var client = services.GetService<IHttpClientFactory>()?.CreateClient() ?? new HttpClient();
                    var outbound = new HttpOutbound(client, uriAddresses[0], disposeClient: services.GetService<IHttpClientFactory>() is null);
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
                    uriAddresses,
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
            // Bind directly from the provided configuration section to avoid surprises with options snapshots.
            var dispatcherConfig = configuration.Get<DispatcherConfig>() ?? new DispatcherConfig();
            MergeLegacyOutbounds(configuration, dispatcherConfig);
            configuration.GetSection("middleware").Bind(dispatcherConfig.Middleware);
            configuration.GetSection("outbounds").Bind(dispatcherConfig.Outbounds);
            return DispatcherConfigMapper.CreateDispatcher(sp, registry, dispatcherConfig, configureOptions);
        });

        services.AddSingleton(sp => sp.GetRequiredService<global::OmniRelay.Dispatcher.Dispatcher>().Codecs);
        services.AddSingleton<IHostedService, DispatcherHostedService>();

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
        services.AddSingleton<IHostedService, DispatcherHostedService>();

        return services;
    }

    private static void MergeLegacyOutbounds(IConfiguration configuration, DispatcherConfig config)
    {
        var outboundsSection = configuration.GetSection("outbounds");
        if (outboundsSection is not IConfigurationSection section || !section.Exists())
        {
            return;
        }

        foreach (var serviceSection in outboundsSection.GetChildren())
        {
            var serviceName = serviceSection.Key;
            if (!config.Outbounds.TryGetValue(serviceName, out var svc))
            {
                svc = new ServiceOutboundsConfig();
                config.Outbounds[serviceName] = svc;
            }

            MergeLegacyRpcShape(serviceSection.GetSection("unary"), svc, rpcKind: "unary");
            MergeLegacyRpcShape(serviceSection.GetSection("oneway"), svc, rpcKind: "oneway");
            MergeLegacyRpcShape(serviceSection.GetSection("stream"), svc, rpcKind: "stream");
            MergeLegacyRpcShape(serviceSection.GetSection("clientStream"), svc, rpcKind: "clientStream");
            MergeLegacyRpcShape(serviceSection.GetSection("duplex"), svc, rpcKind: "duplex");
        }
    }

    private static void MergeLegacyRpcShape(IConfiguration rpcSection, ServiceOutboundsConfig target, string rpcKind)
    {
        if (rpcSection is not IConfigurationSection section || !section.Exists())
        {
            return;
        }

        var httpTargets = section.GetSection("http").Get<List<OutboundTarget>>() ?? [];
        var grpcTargets = section.GetSection("grpc").Get<List<OutboundTarget>>() ?? [];

        foreach (var t in httpTargets)
        {
            switch (rpcKind)
            {
                case "unary": target.Http.Unary.Add(t); break;
                case "oneway": target.Http.Oneway.Add(t); break;
                case "stream": target.Http.Stream.Add(t); break;
                case "clientStream": target.Http.ClientStream.Add(t); break;
                case "duplex": target.Http.Duplex.Add(t); break;
            }
        }

        foreach (var t in grpcTargets)
        {
            switch (rpcKind)
            {
                case "unary": target.Grpc.Unary.Add(t); break;
                case "oneway": target.Grpc.Oneway.Add(t); break;
                case "stream": target.Grpc.Stream.Add(t); break;
                case "clientStream": target.Grpc.ClientStream.Add(t); break;
                case "duplex": target.Grpc.Duplex.Add(t); break;
            }
        }
    }
}

internal sealed class DispatcherHostedService : IHostedService
{
    private readonly global::OmniRelay.Dispatcher.Dispatcher _dispatcher;

    public DispatcherHostedService(global::OmniRelay.Dispatcher.Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _dispatcher.StartOrThrowAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _dispatcher.StopOrThrowAsync(cancellationToken).ConfigureAwait(false);
    }
}
