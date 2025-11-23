using System.Collections.Immutable;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Hugo;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Peers;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Grpc.Interceptors;
using OmniRelay.Transport.Http;
using OmniRelay.Transport.Security;

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

        var interceptorAliases = services.GetService<IGrpcInterceptorAliasRegistry>() ?? CreateDefaultAliasRegistry();

        ApplyInbounds(config.Inbounds, options, interceptorAliases);
        ApplyOutbounds(services, config.Outbounds, options);
        ApplyMiddleware(services, registry, config.Middleware, options);
        ApplyEncodings(config.Encodings, registry, options);

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

    private static void ApplyInbounds(InboundsConfig inbounds, DispatcherOptions options, IGrpcInterceptorAliasRegistry interceptorAliases)
    {
        for (var i = 0; i < inbounds.Http.Count; i++)
        {
            var http = inbounds.Http[i];
            if (http.Urls.Count == 0)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(http.Name) ? $"http-{i}" : http.Name;
            var runtime = http.Runtime ?? new HttpServerRuntimeOptions();
            var tls = ParseHttpTls(http.Tls);

            var inbound = new HttpInbound(
                http.Urls.ToArray(),
                configureServices: null,
                configureApp: null,
                serverRuntimeOptions: runtime,
                serverTlsOptions: tls,
                transportSecurity: null,
                authorizationEvaluator: null);

            options.AddLifecycle(name, inbound);
        }

        for (var i = 0; i < inbounds.Grpc.Count; i++)
        {
            var grpc = inbounds.Grpc[i];
            if (grpc.Urls.Count == 0)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(grpc.Name) ? $"grpc-{i}" : grpc.Name;

            var runtime = ParseGrpcRuntime(grpc.Runtime, interceptorAliases) ?? new GrpcServerRuntimeOptions { EnableDetailedErrors = grpc.EnableDetailedErrors };
            var tls = ParseGrpcTls(grpc.Tls);

            var inbound = new GrpcInbound(
                grpc.Urls.ToArray(),
                configureServices: null,
                serverRuntimeOptions: runtime,
                serverTlsOptions: tls,
                telemetryOptions: null,
                transportSecurity: null,
                authorizationEvaluator: null);

            options.AddLifecycle(name, inbound);
        }
    }

    private static void ApplyOutbounds(IServiceProvider services, OutboundsConfig outbounds, DispatcherOptions options)
    {
        foreach (var (service, set) in outbounds)
        {
            if (set is null)
            {
                continue;
            }
            if (set.Http.Unary.Count == 0 && set.Http.Oneway.Count == 0 && set.Grpc.Unary.Count == 0 &&
                set.Grpc.Oneway.Count == 0 && set.Grpc.Stream.Count == 0 && set.Grpc.ClientStream.Count == 0 && set.Grpc.Duplex.Count == 0)
            {
                continue;
            }

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
            var key = string.IsNullOrWhiteSpace(target.Key) || int.TryParse(target.Key, out _)
                ? "default"
                : target.Key!;

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
                    var outboundResult = HttpOutbound.Create(client, uriAddresses[0], disposeClient: services.GetService<IHttpClientFactory>() is null);
                    var outbound = outboundResult.IsSuccess
                        ? outboundResult.Value
                        : throw new InvalidOperationException($"Failed to create HTTP outbound for service '{service}': {outboundResult.Error?.Message}");
                    if (kind == OutboundKind.Unary)
                    {
                        options.AddUnaryOutbound(service, key, outbound);
                    }
                    else
                    {
                        options.AddOnewayOutbound(service, key, outbound);
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
                        options.AddUnaryOutbound(service, key, outbound);
                        break;
                    case OutboundKind.Oneway:
                        options.AddOnewayOutbound(service, key, outbound);
                        break;
                    case OutboundKind.Stream:
                        options.AddStreamOutbound(service, key, outbound);
                        break;
                    case OutboundKind.ClientStream:
                        options.AddClientStreamOutbound(service, key, outbound);
                        break;
                    case OutboundKind.Duplex:
                        options.AddDuplexOutbound(service, key, outbound);
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

    public static DispatcherConfig LoadConfigFromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var root = configuration as IConfigurationSection ?? configuration;
        var omniSection = root is IConfigurationSection section && section.Path.EndsWith("omnirelay", StringComparison.OrdinalIgnoreCase)
            ? section
            : root.GetSection("omnirelay");

        var effective = omniSection.Exists() ? omniSection : root;

        var service = effective["service"]
                      ?? root["omnirelay:service"]
                      ?? root["service"];

        var config = new DispatcherConfig
        {
            Service = string.IsNullOrWhiteSpace(service) ? "omnirelay" : service,
            Mode = effective["mode"] ?? "InProc",
            Inbounds = ParseInbounds(effective.GetSection("inbounds")),
            Outbounds = ParseOutbounds(effective.GetSection("outbounds")),
            Middleware = ParseMiddleware(effective.GetSection("middleware")),
            Encodings = ParseEncodings(effective.GetSection("encodings"))
        };

        return config;
    }

    private static InboundsConfig ParseInbounds(IConfigurationSection section)
    {
        var inbounds = new InboundsConfig();
        foreach (var http in section.GetSection("http").GetChildren())
        {
            var urls = http.GetSection("urls").GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
            if (urls.Count == 0)
            {
                continue;
            }
            var runtime = ParseHttpRuntime(http.GetSection("runtime"));
            if (runtime.EnableHttp3 == false && bool.TryParse(http["enableHttp3"], out var enableHttp3Inline) && enableHttp3Inline)
            {
                runtime.EnableHttp3 = true;
            }
            inbounds.Http.Add(new HttpInboundConfig
            {
                Name = string.IsNullOrWhiteSpace(http["name"]) ? null : http["name"],
                Urls = urls,
                Runtime = runtime,
                Tls = ParseHttpTlsConfig(http.GetSection("tls"))
            });
        }

        foreach (var grpc in section.GetSection("grpc").GetChildren())
        {
            var urls = grpc.GetSection("urls").GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
            if (urls.Count == 0)
            {
                continue;
            }
            inbounds.Grpc.Add(new GrpcInboundConfig
            {
                Name = string.IsNullOrWhiteSpace(grpc["name"]) ? null : grpc["name"],
                Urls = urls,
                EnableDetailedErrors = bool.TryParse(grpc["enableDetailedErrors"], out var b) ? b : (bool?)null,
                Runtime = ParseGrpcRuntimeConfig(grpc.GetSection("runtime")),
                Tls = ParseGrpcTlsConfig(grpc.GetSection("tls"))
            });
        }

        return inbounds;
    }

    private static OutboundsConfig ParseOutbounds(IConfigurationSection section)
    {
        var result = new OutboundsConfig();
        foreach (var service in section.GetChildren())
        {
            var svc = new ServiceOutboundsConfig();

            // HTTP supports unary/oneway only
            svc.Http.Unary.AddRange(ParseOutboundTargets(service.GetSection("unary").GetSection("http")));
            svc.Http.Oneway.AddRange(ParseOutboundTargets(service.GetSection("oneway").GetSection("http")));

            // gRPC supports all shapes
            svc.Grpc.Unary.AddRange(ParseOutboundTargets(service.GetSection("unary").GetSection("grpc")));
            svc.Grpc.Oneway.AddRange(ParseOutboundTargets(service.GetSection("oneway").GetSection("grpc")));
            svc.Grpc.Stream.AddRange(ParseOutboundTargets(service.GetSection("stream").GetSection("grpc")));
            svc.Grpc.ClientStream.AddRange(ParseOutboundTargets(service.GetSection("clientStream").GetSection("grpc")));
            svc.Grpc.Duplex.AddRange(ParseOutboundTargets(service.GetSection("duplex").GetSection("grpc")));

            result[service.Key] = svc;
        }

        return result;
    }

    private static IEnumerable<OutboundTarget> ParseOutboundTargets(IConfigurationSection section)
    {
        foreach (var targetSection in section.GetChildren())
        {
            var t = new OutboundTarget
            {
                Key = targetSection["key"] ?? targetSection.Key,
                RemoteService = targetSection["remoteService"]
            };

            var url = targetSection["url"];
            if (!string.IsNullOrWhiteSpace(url))
            {
                t.Url = url;
            }
            else
            {
                var addresses = targetSection.GetSection("addresses").GetChildren()
                    .Select(c => c.Value)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v!)
                    .ToList();

                if (addresses.Count == 0)
                {
                    addresses = targetSection.GetSection("endpoints").GetChildren()
                        .Select(ep => ep["address"])
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Select(v => v!)
                        .ToList();
                }

                t.Addresses = addresses;
            }

            yield return t;
        }
    }

    private static MiddlewareConfig ParseMiddleware(IConfigurationSection section)
    {
        var cfg = new MiddlewareConfig();
        cfg.Inbound = ParseMiddlewareStack(section.GetSection("inbound"));
        cfg.Outbound = ParseMiddlewareStack(section.GetSection("outbound"));
        return cfg;
    }

    private static EncodingConfig ParseEncodings(IConfigurationSection section)
    {
        var config = new EncodingConfig();
        config.Json = ParseJsonEncodings(section.GetSection("json"));
        return config;
    }

    private static HttpServerRuntimeOptions ParseHttpRuntime(IConfigurationSection section)
    {
        var runtime = new HttpServerRuntimeOptions
        {
            EnableHttp3 = bool.TryParse(section["enableHttp3"], out var http3) && http3,
            MaxRequestBodySize = TryParseLong(section["maxRequestBodySize"]),
            MaxInMemoryDecodeBytes = TryParseLong(section["maxInMemoryDecodeBytes"]),
            MaxRequestLineSize = TryParseInt(section["maxRequestLineSize"]),
            MaxRequestHeadersTotalSize = TryParseInt(section["maxRequestHeadersTotalSize"]),
            KeepAliveTimeout = TryParseTimeSpan(section["keepAliveTimeout"]),
            RequestHeadersTimeout = TryParseTimeSpan(section["requestHeadersTimeout"]),
            ServerStreamWriteTimeout = TryParseTimeSpan(section["serverStreamWriteTimeout"]),
            DuplexWriteTimeout = TryParseTimeSpan(section["duplexWriteTimeout"]),
            ServerStreamMaxMessageBytes = TryParseInt(section["serverStreamMaxMessageBytes"]),
            DuplexMaxFrameBytes = TryParseInt(section["duplexMaxFrameBytes"]),
            Http3 = ParseHttp3(section.GetSection("http3"))
        };

        return runtime;
    }

    private static HttpServerTlsConfig? ParseHttpTlsConfig(IConfigurationSection section)
    {
        if (!section.Exists())
        {
            return null;
        }

        var path = section["certificatePath"];
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var cfg = new HttpServerTlsConfig
        {
            CertificatePath = path,
            CertificatePassword = section["certificatePassword"],
            CheckCertificateRevocation = TryParseBool(section["checkCertificateRevocation"])
        };

        if (Enum.TryParse<ClientCertificateMode>(section["clientCertificateMode"], ignoreCase: true, out var mode))
        {
            cfg.ClientCertificateMode = mode;
        }

        return cfg;
    }

    private static HttpServerTlsOptions? ParseHttpTls(HttpServerTlsConfig? cfg)
    {
        if (cfg is null)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(cfg.CertificatePath))
        {
            return null;
        }

        var certificate = string.IsNullOrWhiteSpace(cfg.CertificatePassword)
            ? X509CertificateLoader.LoadPkcs12FromFile(cfg.CertificatePath, ReadOnlySpan<char>.Empty, X509KeyStorageFlags.DefaultKeySet)
            : X509CertificateLoader.LoadPkcs12FromFile(cfg.CertificatePath, cfg.CertificatePassword.AsSpan(), X509KeyStorageFlags.DefaultKeySet);

        return new HttpServerTlsOptions
        {
            Certificate = certificate,
            ClientCertificateMode = cfg.ClientCertificateMode,
            CheckCertificateRevocation = cfg.CheckCertificateRevocation
        };
    }

    private static Http3RuntimeOptions? ParseHttp3(IConfigurationSection section)
    {
        if (!section.Exists())
        {
            return null;
        }

        return new Http3RuntimeOptions
        {
            EnableAltSvc = TryParseBool(section["enableAltSvc"]),
            IdleTimeout = TryParseTimeSpan(section["idleTimeout"]),
            KeepAliveInterval = TryParseTimeSpan(section["keepAliveInterval"]),
            MaxBidirectionalStreams = TryParseInt(section["maxBidirectionalStreams"]),
            MaxUnidirectionalStreams = TryParseInt(section["maxUnidirectionalStreams"])
        };
    }

    private static GrpcServerRuntimeConfig? ParseGrpcRuntimeConfig(IConfigurationSection section)
    {
        if (!section.Exists())
        {
            return null;
        }

        var cfg = new GrpcServerRuntimeConfig
        {
            EnableHttp3 = bool.TryParse(section["enableHttp3"], out var enable) && enable,
            MaxReceiveMessageSize = TryParseInt(section["maxReceiveMessageSize"]),
            MaxSendMessageSize = TryParseInt(section["maxSendMessageSize"]),
            KeepAlivePingDelay = TryParseTimeSpan(section["keepAlivePingDelay"]),
            KeepAlivePingTimeout = TryParseTimeSpan(section["keepAlivePingTimeout"]),
            ServerStreamWriteTimeout = TryParseTimeSpan(section["serverStreamWriteTimeout"]),
            DuplexWriteTimeout = TryParseTimeSpan(section["duplexWriteTimeout"]),
            ServerStreamMaxMessageBytes = TryParseInt(section["serverStreamMaxMessageBytes"]),
            DuplexMaxMessageBytes = TryParseInt(section["duplexMaxMessageBytes"]),
            Http3 = ParseHttp3(section.GetSection("http3"))
        };

        foreach (var interceptor in section.GetSection("interceptors").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(interceptor.Value))
            {
                cfg.Interceptors.Add(interceptor.Value);
            }
        }

        return cfg;
    }

    private static GrpcServerTlsConfig? ParseGrpcTlsConfig(IConfigurationSection section)
    {
        if (!section.Exists())
        {
            return null;
        }

        var path = section["certificatePath"];
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var cfg = new GrpcServerTlsConfig
        {
            CertificatePath = path,
            CertificatePassword = section["certificatePassword"],
            CheckCertificateRevocation = TryParseBool(section["checkCertificateRevocation"]),
            EnabledProtocols = ParseSslProtocols(section["enabledProtocols"])
        };

        if (Enum.TryParse<ClientCertificateMode>(section["clientCertificateMode"], ignoreCase: true, out var mode))
        {
            cfg.ClientCertificateMode = mode;
        }

        return cfg;
    }

    private static GrpcServerRuntimeOptions? ParseGrpcRuntime(GrpcServerRuntimeConfig? cfg, IGrpcInterceptorAliasRegistry interceptorAliases)
    {
        if (cfg is null)
        {
            return null;
        }

        return new GrpcServerRuntimeOptions
        {
            EnableHttp3 = cfg.EnableHttp3,
            MaxReceiveMessageSize = cfg.MaxReceiveMessageSize,
            MaxSendMessageSize = cfg.MaxSendMessageSize,
            KeepAlivePingDelay = cfg.KeepAlivePingDelay,
            KeepAlivePingTimeout = cfg.KeepAlivePingTimeout,
            ServerStreamWriteTimeout = cfg.ServerStreamWriteTimeout,
            DuplexWriteTimeout = cfg.DuplexWriteTimeout,
            ServerStreamMaxMessageBytes = cfg.ServerStreamMaxMessageBytes,
            DuplexMaxMessageBytes = cfg.DuplexMaxMessageBytes,
            Http3 = cfg.Http3,
            Interceptors = ResolveInterceptors(cfg.Interceptors, interceptorAliases)
        };
    }

    private static GrpcServerTlsOptions? ParseGrpcTls(GrpcServerTlsConfig? cfg)
    {
        if (cfg is null)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(cfg.CertificatePath))
        {
            return null;
        }

        var certificate = string.IsNullOrWhiteSpace(cfg.CertificatePassword)
            ? X509CertificateLoader.LoadPkcs12FromFile(cfg.CertificatePath, ReadOnlySpan<char>.Empty, X509KeyStorageFlags.DefaultKeySet)
            : X509CertificateLoader.LoadPkcs12FromFile(cfg.CertificatePath, cfg.CertificatePassword.AsSpan(), X509KeyStorageFlags.DefaultKeySet);

        return new GrpcServerTlsOptions
        {
            Certificate = certificate,
            CheckCertificateRevocation = cfg.CheckCertificateRevocation,
            EnabledProtocols = cfg.EnabledProtocols,
            ClientCertificateMode = cfg.ClientCertificateMode
        };
    }

    private static bool? TryParseBool(string? value) =>
        bool.TryParse(value, out var parsed) ? parsed : (bool?)null;

    private static int? TryParseInt(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : (int?)null;

    private static long? TryParseLong(string? value) =>
        long.TryParse(value, out var parsed) ? parsed : (long?)null;

    private static SslProtocols? ParseSslProtocols(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<SslProtocols>(value, ignoreCase: true, out var parsed) ? parsed : null;
    }

    private static readonly Dictionary<string, Type> KnownInterceptors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["logging"] = typeof(GrpcServerLoggingInterceptor),
        ["grpc.logging"] = typeof(GrpcServerLoggingInterceptor),
        ["exception"] = typeof(GrpcExceptionAdapterInterceptor),
        ["transport-security"] = typeof(TransportSecurityGrpcInterceptor)
    };

    private static List<Type> ResolveInterceptors(IEnumerable<string> aliases, IGrpcInterceptorAliasRegistry registry)
    {
        var list = new List<Type>();
        foreach (var alias in aliases)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                continue;
            }

            if (KnownInterceptors.TryGetValue(alias, out var known))
            {
                list.Add(known);
                continue;
            }

            if (registry.TryResolveServer(alias, out var resolved))
            {
                list.Add(resolved);
                continue;
            }

            throw new InvalidOperationException($"Unknown gRPC server interceptor alias '{alias}'. Register it via IGrpcInterceptorAliasRegistry or use a built-in alias.");
        }

        return list;
    }

    internal static GrpcInterceptorAliasRegistry CreateDefaultAliasRegistry()
    {
        var registry = new GrpcInterceptorAliasRegistry();
        foreach (var kvp in KnownInterceptors)
        {
            registry.RegisterServer(kvp.Key, kvp.Value);
        }

        return registry;
    }

    private static TimeSpan? TryParseTimeSpan(string? value) =>
            TimeSpan.TryParse(value, out var parsed) ? parsed : (TimeSpan?)null;

    private static JsonEncodingConfig ParseJsonEncodings(IConfigurationSection section)
    {
        var cfg = new JsonEncodingConfig();

        var profilesSection = section.GetSection("profiles");
        foreach (var profileSection in profilesSection.GetChildren())
        {
            var profile = new JsonProfileConfig
            {
                Name = profileSection.Key
            };

            var optionsSection = profileSection.GetSection("options");
            if (bool.TryParse(optionsSection["writeIndented"], out var writeIndented))
            {
                profile.WriteIndented = writeIndented;
            }

            cfg.Profiles[profile.Name] = profile;
        }

        foreach (var outboundSection in section.GetSection("outbound").GetChildren())
        {
            cfg.Outbound.Add(new JsonOutboundEncodingConfig
            {
                Service = outboundSection["service"],
                Procedure = outboundSection["procedure"],
                Kind = outboundSection["kind"],
                Profile = outboundSection["profile"],
                Encoding = outboundSection["encoding"],
                CodecKey = outboundSection["codecKey"]
            });
        }

        return cfg;
    }

    private static MiddlewareStackConfig ParseMiddlewareStack(IConfigurationSection section)
    {
        var stack = new MiddlewareStackConfig();
        stack.Unary.AddRange(ReadList(section.GetSection("unary")));
        stack.Oneway.AddRange(ReadList(section.GetSection("oneway")));
        stack.Stream.AddRange(ReadList(section.GetSection("stream")));
        stack.ClientStream.AddRange(ReadList(section.GetSection("clientStream")));
        stack.Duplex.AddRange(ReadList(section.GetSection("duplex")));
        return stack;
    }

    private static IEnumerable<string> ReadList(IConfigurationSection section) =>
        section.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!);

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

            object? middleware = inbound
                ? registry.TryResolveInbound(key, kind, services, out var resolvedInbound) && resolvedInbound is not null
                    ? resolvedInbound
                    : null
                : registry.TryResolveOutbound(key, kind, services, out var resolvedOutbound) ? resolvedOutbound : null;

            if (middleware is not null)
            {
                switch (kind)
                {
                    case ProcedureKind.Unary when inbound:
                        options.UnaryInboundMiddleware.Add((IUnaryInboundMiddleware)middleware);
                        break;
                    case ProcedureKind.Unary:
                        options.UnaryOutboundMiddleware.Add((IUnaryOutboundMiddleware)middleware);
                        break;
                    case ProcedureKind.Oneway when inbound:
                        options.OnewayInboundMiddleware.Add((IOnewayInboundMiddleware)middleware);
                        break;
                    case ProcedureKind.Oneway:
                        options.OnewayOutboundMiddleware.Add((IOnewayOutboundMiddleware)middleware);
                        break;
                    case ProcedureKind.Stream when inbound:
                        options.StreamInboundMiddleware.Add((IStreamInboundMiddleware)middleware);
                        break;
                    case ProcedureKind.Stream:
                        options.StreamOutboundMiddleware.Add((IStreamOutboundMiddleware)middleware);
                        break;
                    case ProcedureKind.ClientStream when inbound:
                        options.ClientStreamInboundMiddleware.Add((IClientStreamInboundMiddleware)middleware);
                        break;
                    case ProcedureKind.ClientStream:
                        options.ClientStreamOutboundMiddleware.Add((IClientStreamOutboundMiddleware)middleware);
                        break;
                    case ProcedureKind.Duplex when inbound:
                        options.DuplexInboundMiddleware.Add((IDuplexInboundMiddleware)middleware);
                        break;
                    case ProcedureKind.Duplex:
                        options.DuplexOutboundMiddleware.Add((IDuplexOutboundMiddleware)middleware);
                        break;
                }
            }
        }
    }

    private static void ApplyEncodings(EncodingConfig encodings, DispatcherComponentRegistry registry, DispatcherOptions options)
    {
        if (encodings?.Json is null)
        {
            return;
        }

        foreach (var outbound in encodings.Json.Outbound)
        {
            if (outbound is null || string.IsNullOrWhiteSpace(outbound.Procedure))
            {
                continue;
            }

            var key = outbound.CodecKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var kind = ParseProcedureKind(outbound.Kind);
            if (registry.TryResolveOutboundCodec(key!, kind, out var action) && action is not null)
            {
                var service = string.IsNullOrWhiteSpace(outbound.Service) ? options.ServiceName : outbound.Service!;
                var procedure = outbound.Procedure ?? options.ServiceName;
                action(options, service, procedure);
            }
        }
    }

    private static ProcedureKind ParseProcedureKind(string? value)
    {
        if (Enum.TryParse<ProcedureKind>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return ProcedureKind.Unary;
    }

}

public static class DispatcherConfigServiceCollectionExtensions
{
    public static IServiceCollection AddOmniRelayDispatcherFromConfiguration(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<DispatcherComponentRegistry>? registerComponents = null,
        Action<IServiceProvider, DispatcherOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<IGrpcInterceptorAliasRegistry>(_ => DispatcherConfigMapper.CreateDefaultAliasRegistry());
        services.AddDispatcherComponentRegistry(registry => registerComponents?.Invoke(registry));
        services.AddSingleton(sp =>
        {
            var registry = sp.GetRequiredService<DispatcherComponentRegistry>();
            var dispatcherConfig = DispatcherConfigMapper.LoadConfigFromConfiguration(configuration);
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
            var loadResult = LoadDispatcherConfig(configPath);
            if (loadResult.IsFailure)
            {
                throw OmniRelay.Errors.OmniRelayErrors.FromError(loadResult.Error!, "dispatcher-config");
            }

            return DispatcherConfigMapper.CreateDispatcher(sp, registry, loadResult.Value, configureOptions);
        });

        services.AddSingleton(sp => sp.GetRequiredService<global::OmniRelay.Dispatcher.Dispatcher>().Codecs);
        services.AddSingleton<IHostedService, DispatcherHostedService>();

        return services;
    }

    private static Result<DispatcherConfig> LoadDispatcherConfig(string configPath)
    {
        return Result.Try(() =>
            {
                var json = File.ReadAllText(configPath);
                return json;
            })
            .Then(json =>
            {
                try
                {
                    var config = JsonSerializer.Deserialize(json, DispatcherConfigJsonContext.Default.DispatcherConfig);
                    if (config is null)
                    {
                        return Result.Fail<DispatcherConfig>(
                            Error.From($"Failed to deserialize dispatcher config from {configPath}.", "dispatcher.config.deserialization_failed")
                                 .WithMetadata("path", configPath));
                    }

                    return Result.Ok(config);
                }
                catch (JsonException ex)
                {
                    return Result.Fail<DispatcherConfig>(
                        Error.FromException(ex)
                            .WithMetadata("path", configPath)
                            .WithMetadata("code", "dispatcher.config.invalid_json"));
                }
            });
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
        var startResult = await _dispatcher.StartAsync(cancellationToken).ConfigureAwait(false);
        if (startResult.IsFailure && startResult.Error is { } error)
        {
            var exception = OmniRelay.Errors.OmniRelayErrors.FromError(error, "host");
            throw exception;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var stopResult = await _dispatcher.StopAsync(cancellationToken).ConfigureAwait(false);
        if (stopResult.IsFailure && stopResult.Error is { } error)
        {
            var exception = OmniRelay.Errors.OmniRelayErrors.FromError(error, "host");
            throw exception;
        }
    }
}

internal static class DispatcherConfigMapperResultExtensions
{
    public static Result<T> ToResult<T>(this T value) => Result.Ok(value);
}
