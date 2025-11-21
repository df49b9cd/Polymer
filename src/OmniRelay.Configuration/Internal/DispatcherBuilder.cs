using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Grpc.Core.Interceptors;
using Json.Schema;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OmniRelay.Configuration.Models;
using OmniRelay.ControlPlane.Bootstrap;
using OmniRelay.ControlPlane.Hosting;
using OmniRelay.ControlPlane.Security;
using OmniRelay.ControlPlane.Upgrade;
using OmniRelay.Core;
using OmniRelay.Core.Diagnostics;
using OmniRelay.Core.Gossip;
using OmniRelay.Core.Leadership;
using OmniRelay.Core.Peers;
using OmniRelay.Core.Shards.ControlPlane;
using OmniRelay.Core.Transport;
using OmniRelay.Diagnostics;
using OmniRelay.Dispatcher;
using OmniRelay.Security.Authorization;
using OmniRelay.Security.Secrets;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;
using OmniRelay.Transport.Security;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace OmniRelay.Configuration.Internal;

/// <summary>
/// Builds a configured <see cref="Dispatcher.Dispatcher"/> from bound <see cref="Models.OmniRelayConfigurationOptions"/> and the service provider.
/// </summary>
internal sealed partial class DispatcherBuilder
{
    private readonly OmniRelayConfigurationOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly Dictionary<string, HttpOutbound> _httpOutboundCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GrpcOutbound> _grpcOutboundCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ICustomInboundSpec> _customInboundSpecs;
    private readonly Dictionary<string, ICustomOutboundSpec> _customOutboundSpecs;
    private readonly Dictionary<string, ICustomPeerChooserSpec> _customPeerSpecs;
    private readonly ISecretProvider? _secretProvider;
    private readonly TransportSecurityPolicyEvaluator? _transportSecurityEvaluator;
    private readonly MeshAuthorizationEvaluator? _authorizationEvaluator;
    private readonly bool _nativeAotEnabled;
    private readonly bool _nativeAotStrict;
    private static readonly string[] MeshGossipDependency = ["mesh-gossip"];
    private static readonly string[] MeshLeadershipDependency = ["mesh-leadership"];

    public DispatcherBuilder(OmniRelayConfigurationOptions options, IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _secretProvider = _serviceProvider.GetService<ISecretProvider>();
        _transportSecurityEvaluator = _serviceProvider.GetService<TransportSecurityPolicyEvaluator>();
        _authorizationEvaluator = _serviceProvider.GetService<MeshAuthorizationEvaluator>();
        _nativeAotEnabled = options.NativeAot?.Enabled == true;
        _nativeAotStrict = options.NativeAot?.Strict ?? true;

        _customInboundSpecs = _serviceProvider
            .GetServices<ICustomInboundSpec>()
            .ToDictionary(spec => spec.Name, StringComparer.OrdinalIgnoreCase);

        _customOutboundSpecs = _serviceProvider
            .GetServices<ICustomOutboundSpec>()
            .ToDictionary(spec => spec.Name, StringComparer.OrdinalIgnoreCase);

        _customPeerSpecs = _serviceProvider
            .GetServices<ICustomPeerChooserSpec>()
            .ToDictionary(spec => spec.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Constructs a new dispatcher based on the current options and DI configuration.</summary>
    public Dispatcher.Dispatcher Build()
    {
        if (_nativeAotEnabled)
        {
            return BuildNativeAot();
        }

#pragma warning disable IL2026, IL3050 // Reflection-heavy path is intentional for non-AOT builds.
        return BuildReflection();
#pragma warning restore IL2026, IL3050
    }

    /// <summary>Legacy reflection-heavy bootstrap path (kept for compatibility).</summary>
    [RequiresDynamicCode("OmniRelay dispatcher bootstrapping relies on reflection-heavy configuration binding.")]
    [RequiresUnreferencedCode("OmniRelay dispatcher bootstrapping relies on reflection-heavy configuration binding.")]
    private Dispatcher.Dispatcher BuildReflection()
    {
        var serviceName = _options.Service?.Trim();
        if (string.IsNullOrEmpty(serviceName))
        {
            throw new OmniRelayConfigurationException("OmniRelay configuration must specify a service name (service).");
        }

        var dispatcherOptions = new DispatcherOptions(serviceName);

        ApplyMiddleware(dispatcherOptions);
        ConfigureInbounds(dispatcherOptions);
        ConfigureOutbounds(dispatcherOptions);
        ConfigureEncodings(dispatcherOptions);
        ConfigureMeshComponents(dispatcherOptions);

        return new Dispatcher.Dispatcher(dispatcherOptions);
    }

    /// <summary>Trimming/AOT-friendly bootstrap path that avoids runtime type scanning.</summary>
    private Dispatcher.Dispatcher BuildNativeAot()
    {
        var serviceName = _options.Service?.Trim();
        if (string.IsNullOrEmpty(serviceName))
        {
            throw new OmniRelayConfigurationException("OmniRelay configuration must specify a service name (service).");
        }

        var dispatcherOptions = new DispatcherOptions(serviceName);

        ApplyMiddlewareNative(dispatcherOptions);
        ConfigureInbounds(dispatcherOptions, allowCustom: !_nativeAotStrict);
        ConfigureOutbounds(dispatcherOptions, allowCustom: !_nativeAotStrict);
        ValidateNativeEncodings();
        ConfigureMeshComponents(dispatcherOptions);

        return new Dispatcher.Dispatcher(dispatcherOptions);
    }

    private void ConfigureInbounds(DispatcherOptions dispatcherOptions, bool allowCustom = true)
    {
        ConfigureHttpInbounds(dispatcherOptions);
        ConfigureGrpcInbounds(dispatcherOptions);
        if (allowCustom)
        {
#pragma warning disable IL2026, IL3050 // Custom inbound specs rely on reflection and are disabled in strict AOT mode.
            ConfigureCustomInbounds(dispatcherOptions);
#pragma warning restore IL2026, IL3050
            return;
        }

        if (_customInboundSpecs.Count > 0 && _nativeAotStrict)
        {
            throw new OmniRelayConfigurationException("Custom inbounds are not supported while native AOT mode is enabled.");
        }
    }

    private void ConfigureHttpInbounds(DispatcherOptions dispatcherOptions)
    {
        var index = 0;
        foreach (var inbound in _options.Inbounds.Http)
        {
            if (inbound.Urls.Count == 0)
            {
                throw new OmniRelayConfigurationException($"HTTP inbound at index {index} must specify at least one url.");
            }

            var urls = inbound.Urls
                .Select((url, position) => ValidateHttpUrl(url, $"http inbound #{index} (entry {position})"))
                .ToArray();

            if (urls.Length == 0)
            {
                throw new OmniRelayConfigurationException($"HTTP inbound at index {index} resolved to zero valid urls.");
            }

            var name = string.IsNullOrWhiteSpace(inbound.Name)
                ? $"http-inbound:{index}"
                : inbound.Name!;

            var requiresTls = urls.Any(static url =>
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    return false;
                }

                return uri.Scheme == Uri.UriSchemeHttps;
            });

            var httpRuntimeOptions = BuildHttpServerRuntimeOptions(inbound.Runtime);
            var httpTlsOptions = BuildHttpServerTlsOptions(inbound.Tls);

            if (requiresTls && httpTlsOptions?.Certificate is null)
            {
                throw new OmniRelayConfigurationException(
                    $"HTTP inbound '{name}' includes HTTPS endpoints but no TLS certificate was configured. Provide inbounds:http:{index}:tls:certificatePath.");
            }

            var configureServices = CreateHttpInboundServiceConfigurator();
            var configureApp = CreateHttpInboundAppConfigurator();
            var httpInbound = new HttpInbound(
                urls,
                configureServices: configureServices,
                configureApp: configureApp,
                serverRuntimeOptions: httpRuntimeOptions,
                serverTlsOptions: httpTlsOptions,
                transportSecurity: _transportSecurityEvaluator,
                authorizationEvaluator: _authorizationEvaluator);
            dispatcherOptions.AddLifecycle(name, httpInbound);
            RegisterDrainParticipant(name, httpInbound);
            index++;
        }
    }

    private void ConfigureGrpcInbounds(DispatcherOptions dispatcherOptions)
    {
        var index = 0;
        var configureServices = CreateGrpcInboundServiceConfigurator();
        foreach (var inbound in _options.Inbounds.Grpc)
        {
            if (inbound.Urls.Count == 0)
            {
                throw new OmniRelayConfigurationException($"gRPC inbound at index {index} must specify at least one url.");
            }

            var urls = inbound.Urls
                .Select((url, position) => ValidateGrpcUrl(url, $"grpc inbound #{index} (entry {position})"))
                .ToArray();

            var runtimeOptions = BuildGrpcServerRuntimeOptions(inbound.Runtime);
            var tlsOptions = BuildGrpcServerTlsOptions(inbound.Tls);
            var telemetryOptions = BuildGrpcTelemetryOptions(inbound.Telemetry, serverSide: true);

            var name = string.IsNullOrWhiteSpace(inbound.Name)
                ? $"grpc-inbound:{index}"
                : inbound.Name!;

            var grpcInbound = new GrpcInbound(
                urls,
                configureServices: configureServices,
                serverRuntimeOptions: runtimeOptions,
                serverTlsOptions: tlsOptions,
                telemetryOptions: telemetryOptions,
                transportSecurity: _transportSecurityEvaluator,
                authorizationEvaluator: _authorizationEvaluator);
            dispatcherOptions.AddLifecycle(name, grpcInbound);
            RegisterDrainParticipant(name, grpcInbound);

            index++;
        }
    }

    [RequiresUnreferencedCode("Calls Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue<T>(String)")]
    private void ConfigureCustomInbounds(DispatcherOptions dispatcherOptions)
    {
        if (_customInboundSpecs.Count == 0)
        {
            return;
        }

        var customSection = _configuration.GetSection("inbounds:custom");
        if (!customSection.Exists())
        {
            return;
        }

        var index = 0;
        foreach (var section in customSection.GetChildren())
        {
            var specName = section.GetValue<string>("spec");
            if (string.IsNullOrWhiteSpace(specName))
            {
                throw new OmniRelayConfigurationException($"Custom inbound at index {index} must specify a 'spec' value.");
            }

            if (!_customInboundSpecs.TryGetValue(specName, out var spec))
            {
                throw new OmniRelayConfigurationException($"No custom inbound spec registered for '{specName}'.");
            }

            var inbound = spec.CreateInbound(section, _serviceProvider)
                ?? throw new OmniRelayConfigurationException($"Custom inbound spec '{specName}' returned null.");

            var name = section.GetValue<string>("name");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"custom-inbound:{specName}:{index}";
            }

            dispatcherOptions.AddLifecycle(name!, inbound);
            index++;
        }
    }

    private void ConfigureOutbounds(DispatcherOptions dispatcherOptions, bool allowCustom = true)
    {
        foreach (var (service, config) in _options.Outbounds)
        {
            if (string.IsNullOrWhiteSpace(service))
            {
                continue;
            }

            var serviceSection = _configuration.GetSection($"outbounds:{service}");

            RegisterOutboundSet(dispatcherOptions, service, config.Unary, serviceSection, OutboundKind.Unary, allowCustom);
            RegisterOutboundSet(dispatcherOptions, service, config.Oneway, serviceSection, OutboundKind.Oneway, allowCustom);
            RegisterOutboundSet(dispatcherOptions, service, config.Stream, serviceSection, OutboundKind.Stream, allowCustom);
            RegisterOutboundSet(dispatcherOptions, service, config.ClientStream, serviceSection, OutboundKind.ClientStream, allowCustom);
            RegisterOutboundSet(dispatcherOptions, service, config.Duplex, serviceSection, OutboundKind.Duplex, allowCustom);
        }
    }

    private void RegisterOutboundSet(
        DispatcherOptions dispatcherOptions,
        string service,
        RpcOutboundConfiguration? configuration,
        IConfigurationSection? serviceSection,
        OutboundKind kind,
        bool allowCustom)
    {
        if (configuration is null)
        {
            return;
        }

        var kindSectionName = GetOutboundSectionName(kind);
        var kindSection = serviceSection?.GetSection(kindSectionName);

        foreach (var http in configuration.Http)
        {
            if (kind is OutboundKind.Stream or OutboundKind.ClientStream or OutboundKind.Duplex)
            {
                throw new OmniRelayConfigurationException(
                    $"HTTP outbound cannot satisfy {kind.ToString().ToLowerInvariant()} RPCs for service '{service}'.");
            }

            var outbound = CreateHttpOutbound(service, http);
            switch (kind)
            {
                case OutboundKind.Unary:
                    dispatcherOptions.AddUnaryOutbound(service, http.Key, outbound);
                    break;
                case OutboundKind.Oneway:
                    dispatcherOptions.AddOnewayOutbound(service, http.Key, outbound);
                    break;
                case OutboundKind.Stream:
                case OutboundKind.ClientStream:
                case OutboundKind.Duplex:
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }

        var grpcIndex = 0;
        foreach (var grpc in configuration.Grpc)
        {
            var outboundSection = kindSection?.GetSection("grpc").GetSection(grpcIndex.ToString(CultureInfo.InvariantCulture))
                                  ?? kindSection?.GetSection(grpcIndex.ToString(CultureInfo.InvariantCulture));

            var outbound = CreateGrpcOutbound(service, grpc, outboundSection);
            switch (kind)
            {
                case OutboundKind.Unary:
                    dispatcherOptions.AddUnaryOutbound(service, grpc.Key, outbound);
                    break;
                case OutboundKind.Oneway:
                    dispatcherOptions.AddOnewayOutbound(service, grpc.Key, outbound);
                    break;
                case OutboundKind.Stream:
                    dispatcherOptions.AddStreamOutbound(service, grpc.Key, outbound);
                    break;
                case OutboundKind.ClientStream:
                    dispatcherOptions.AddClientStreamOutbound(service, grpc.Key, outbound);
                    break;
                case OutboundKind.Duplex:
                    dispatcherOptions.AddDuplexOutbound(service, grpc.Key, outbound);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }

            grpcIndex++;
        }

        if (!allowCustom)
        {
            if (_customOutboundSpecs.Count > 0 && _nativeAotStrict)
            {
                throw new OmniRelayConfigurationException("Custom outbound specs are not supported while native AOT mode is enabled.");
            }

            return;
        }

#pragma warning disable IL2026, IL3050 // Custom outbound specs rely on reflection and are disabled in strict AOT mode.
        ConfigureCustomOutbounds(dispatcherOptions, service, kind, kindSection);
#pragma warning restore IL2026, IL3050
    }

    private static string GetOutboundSectionName(OutboundKind kind) => kind switch
    {
        OutboundKind.Unary => "unary",
        OutboundKind.Oneway => "oneway",
        OutboundKind.Stream => "stream",
        OutboundKind.ClientStream => "clientStream",
        OutboundKind.Duplex => "duplex",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    [RequiresUnreferencedCode("Calls Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue<T>(String)")]
    private void ConfigureCustomOutbounds(
        DispatcherOptions dispatcherOptions,
        string service,
        OutboundKind kind,
        IConfigurationSection? kindSection)
    {
        if (_customOutboundSpecs.Count == 0 || kindSection is null)
        {
            return;
        }

        var customSection = kindSection.GetSection("custom");
        if (!customSection.Exists())
        {
            return;
        }

        var index = 0;
        foreach (var section in customSection.GetChildren())
        {
            var specName = section.GetValue<string>("spec");
            if (string.IsNullOrWhiteSpace(specName))
            {
                throw new OmniRelayConfigurationException($"Custom outbound at index {index} for service '{service}' must specify a 'spec' value.");
            }

            if (!_customOutboundSpecs.TryGetValue(specName, out var spec))
            {
                throw new OmniRelayConfigurationException($"No custom outbound spec registered for '{specName}'.");
            }

            var key = section.GetValue<string>("key");

            switch (kind)
            {
                case OutboundKind.Unary:
                    {
                        var outbound = spec.CreateUnaryOutbound(section, _serviceProvider)
                            ?? throw new OmniRelayConfigurationException($"Custom outbound spec '{specName}' did not provide a unary outbound.");
                        dispatcherOptions.AddUnaryOutbound(service, key, outbound);
                        break;
                    }
                case OutboundKind.Oneway:
                    {
                        var outbound = spec.CreateOnewayOutbound(section, _serviceProvider)
                            ?? throw new OmniRelayConfigurationException($"Custom outbound spec '{specName}' did not provide a oneway outbound.");
                        dispatcherOptions.AddOnewayOutbound(service, key, outbound);
                        break;
                    }
                case OutboundKind.Stream:
                    {
                        var outbound = spec.CreateStreamOutbound(section, _serviceProvider)
                            ?? throw new OmniRelayConfigurationException($"Custom outbound spec '{specName}' did not provide a stream outbound.");
                        dispatcherOptions.AddStreamOutbound(service, key, outbound);
                        break;
                    }
                case OutboundKind.ClientStream:
                    {
                        var outbound = spec.CreateClientStreamOutbound(section, _serviceProvider)
                            ?? throw new OmniRelayConfigurationException($"Custom outbound spec '{specName}' did not provide a client stream outbound.");
                        dispatcherOptions.AddClientStreamOutbound(service, key, outbound);
                        break;
                    }
                case OutboundKind.Duplex:
                    {
                        var outbound = spec.CreateDuplexOutbound(section, _serviceProvider)
                            ?? throw new OmniRelayConfigurationException($"Custom outbound spec '{specName}' did not provide a duplex outbound.");
                        dispatcherOptions.AddDuplexOutbound(service, key, outbound);
                        break;
                    }
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }

            index++;
        }
    }

    private HttpOutbound CreateHttpOutbound(string service, HttpOutboundTargetConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.Url))
        {
            throw new OmniRelayConfigurationException($"HTTP outbound for service '{service}' must specify a url.");
        }

        var uri = ValidateHttpUrl(configuration.Url!, $"http outbound for service '{service}'");
        var runtimeOptions = BuildHttpClientRuntimeOptions(configuration.Runtime);
        var runtimeKey = runtimeOptions is null
            ? "default"
            : FormattableString.Invariant($"http3={runtimeOptions.EnableHttp3};ver={runtimeOptions.RequestVersion?.ToString() ?? "null"};policy={runtimeOptions.VersionPolicy?.ToString() ?? "null"}");

        var cacheKey = FormattableString.Invariant($"{service}|{configuration.Key ?? OutboundRegistry.DefaultKey}|{uri}|{configuration.ClientName ?? string.Empty}|{runtimeKey}");

        if (_httpOutboundCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var (client, dispose) = CreateHttpClient(configuration.ClientName);
        var outbound = new HttpOutbound(client, new Uri(uri, UriKind.Absolute), dispose, runtimeOptions);
        _httpOutboundCache[cacheKey] = outbound;
        return outbound;
    }

    private (HttpClient Client, bool Dispose) CreateHttpClient(string? clientName)
    {
        var factory = _serviceProvider.GetService<IHttpClientFactory>();
        if (factory is null)
        {
            return (new HttpClient(), true);
        }

        var name = string.IsNullOrWhiteSpace(clientName) ? string.Empty : clientName!;
        var client = string.IsNullOrEmpty(name)
            ? factory.CreateClient()
            : factory.CreateClient(name);
        return (client, false);

    }

    private static HttpClientRuntimeOptions? BuildHttpClientRuntimeOptions(HttpClientRuntimeConfiguration configuration)
    {
        var enableHttp3 = configuration.EnableHttp3 ?? false;
        var version = ParseHttpVersion(configuration.RequestVersion);
        var policy = ParseHttpVersionPolicy(configuration.VersionPolicy);

        if (!enableHttp3 && version is null && policy is null)
        {
            return null;
        }

        return new HttpClientRuntimeOptions
        {
            EnableHttp3 = enableHttp3,
            RequestVersion = version,
            VersionPolicy = policy
        };
    }

    private static Version? ParseHttpVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Version.TryParse(value, out var parsed) ? parsed : throw new OmniRelayConfigurationException($"Unrecognized HTTP version '{value}'.");
    }

    private static HttpVersionPolicy? ParseHttpVersionPolicy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "requestversionexact" or "request-version-exact" or "exact" => HttpVersionPolicy.RequestVersionExact,
            "requestversionorhigher" or "request-version-or-higher" or "orhigher" or "higher" => HttpVersionPolicy.RequestVersionOrHigher,
            "requestversionlower" or "requestversionorlower" or
            "request-version-lower" or "request-version-or-lower" or
            "orlower" or "lower" => HttpVersionPolicy.RequestVersionOrLower,
            _ => throw new OmniRelayConfigurationException($"Unrecognized HTTP version policy '{value}'.")
        };
    }

    private GrpcOutbound CreateGrpcOutbound(string service, GrpcOutboundTargetConfiguration configuration, IConfigurationSection? configurationSection)
    {
        var endpoints = configuration.Endpoints;
        var hasEndpoints = endpoints is { Count: > 0 };
        if (configuration.Addresses.Count == 0 && !hasEndpoints)
        {
            throw new OmniRelayConfigurationException($"gRPC outbound for service '{service}' must specify peer addresses.");
        }

        // Build peer URI list and capture per-endpoint H3 capabilities when provided.
        IReadOnlyDictionary<Uri, bool>? h3Support = null;
        Uri[] uris;

        if (hasEndpoints)
        {
            var map = new Dictionary<Uri, bool>();
            var list = new List<Uri>(endpoints!.Count);
            var index = 0;
            foreach (var ep in endpoints!)
            {
                if (string.IsNullOrWhiteSpace(ep.Address))
                {
                    index++;
                    continue;
                }

                var validated = ValidateGrpcUrl(ep.Address!, $"grpc outbound endpoint #{index} for service '{service}'");
                var uri = new Uri(validated, UriKind.Absolute);
                list.Add(uri);
                var supports = ep.SupportsHttp3 ?? false;
                // Only consider HTTP/3 support hints for HTTPS endpoints
                if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    supports = false;
                }
                map[uri] = supports;
                index++;
            }

            // Prefer HTTP/3 capable endpoints first in ordering to improve baseline distribution
            uris = [.. list.OrderByDescending(u => map.TryGetValue(u, out var s) && s)];

            if (map.Count > 0)
            {
                h3Support = map;
            }
        }
        else
        {
            uris = [.. configuration.Addresses
                .Select((address, index) => ValidateGrpcUrl(address, $"grpc outbound peer #{index} for service '{service}'"))
                .Select(value => new Uri(value, UriKind.Absolute))];
        }

        var remoteService = string.IsNullOrWhiteSpace(configuration.RemoteService)
            ? service
            : configuration.RemoteService!;

        var peerSection = configurationSection?.GetSection("peer");
        var chooserFactory = CreatePeerChooserFactory(configuration.PeerChooser, configuration.Peer, peerSection);
        var breakerOptions = BuildCircuitBreakerOptions(configuration.CircuitBreaker);
        var telemetryOptions = BuildGrpcTelemetryOptions(configuration.Telemetry, serverSide: false);
        var runtimeOptions = BuildGrpcClientRuntimeOptions(configuration.Runtime);
        var tlsOptions = BuildGrpcClientTlsOptions(configuration.Tls);

        if (configuration.Peer?.Spec is not null)
        {
            return new GrpcOutbound(
                uris,
                remoteService,
                clientTlsOptions: tlsOptions,
                peerChooser: chooserFactory,
                clientRuntimeOptions: runtimeOptions,
                peerCircuitBreakerOptions: breakerOptions,
                telemetryOptions: telemetryOptions,
                endpointHttp3Support: h3Support);
        }

        var cacheKey = FormattableString.Invariant($"{service}|{configuration.Key ?? OutboundRegistry.DefaultKey}|{remoteService}|{string.Join(",", uris.Select(u => u.ToString()))}|{configuration.PeerChooser ?? "round-robin"}");

        if (_grpcOutboundCache.TryGetValue(cacheKey, out var cachedOutbound))
        {
            return cachedOutbound;
        }

        var outboundCached = new GrpcOutbound(
            uris,
            remoteService,
            clientTlsOptions: tlsOptions,
            peerChooser: chooserFactory,
            clientRuntimeOptions: runtimeOptions,
            peerCircuitBreakerOptions: breakerOptions,
            telemetryOptions: telemetryOptions,
            endpointHttp3Support: h3Support);

        _grpcOutboundCache[cacheKey] = outboundCached;
        return outboundCached;
    }

    private Func<IReadOnlyList<IPeer>, IPeerChooser> CreatePeerChooserFactory(
        string? peerChooser,
        PeerSpecConfiguration? peerSpec,
        IConfigurationSection? peerSection)
    {
        if (peerSpec is { Spec: { Length: > 0 } specName })
        {
            if (!_customPeerSpecs.TryGetValue(specName, out var spec))
            {
                throw new OmniRelayConfigurationException($"No custom peer chooser spec registered for '{specName}'.");
            }

            if (peerSection is null)
            {
                throw new OmniRelayConfigurationException($"Peer spec '{specName}' requires configuration under 'peer'.");
            }

            return spec.CreateFactory(peerSection, _serviceProvider);
        }

        if (string.IsNullOrWhiteSpace(peerChooser) ||
            peerChooser.Equals("round-robin", StringComparison.OrdinalIgnoreCase) ||
            peerChooser.Equals("roundrobin", StringComparison.OrdinalIgnoreCase))
        {
            return peers => new RoundRobinPeerChooser(ImmutableArray.CreateRange(peers));
        }

        if (peerChooser.Equals("fewest-pending", StringComparison.OrdinalIgnoreCase) ||
            peerChooser.Equals("least-pending", StringComparison.OrdinalIgnoreCase))
        {
            return peers => new FewestPendingPeerChooser(ImmutableArray.CreateRange(peers));
        }

        if (peerChooser.Equals("two-random-choice", StringComparison.OrdinalIgnoreCase) ||
            peerChooser.Equals("two-random-choices", StringComparison.OrdinalIgnoreCase) ||
            peerChooser.Equals("2-random", StringComparison.OrdinalIgnoreCase))
        {
            return peers => new TwoRandomPeerChooser(ImmutableArray.CreateRange(peers));
        }

        throw new OmniRelayConfigurationException(
            $"Unsupported peer chooser '{peerChooser}'. Supported values: round-robin, fewest-pending, two-random-choice.");
    }

    private static PeerCircuitBreakerOptions? BuildCircuitBreakerOptions(PeerCircuitBreakerConfiguration configuration)
    {
        if (configuration is null)
        {
            return null;
        }

        var hasValues =
            configuration.BaseDelay.HasValue ||
            configuration.MaxDelay.HasValue ||
            configuration.FailureThreshold.HasValue ||
            configuration.HalfOpenMaxAttempts.HasValue ||
            configuration.HalfOpenSuccessThreshold.HasValue;

        if (!hasValues)
        {
            return null;
        }

        var defaults = new PeerCircuitBreakerOptions();

        return new PeerCircuitBreakerOptions
        {
            BaseDelay = configuration.BaseDelay ?? defaults.BaseDelay,
            MaxDelay = configuration.MaxDelay ?? defaults.MaxDelay,
            FailureThreshold = configuration.FailureThreshold ?? defaults.FailureThreshold,
            HalfOpenMaxAttempts = configuration.HalfOpenMaxAttempts ?? defaults.HalfOpenMaxAttempts,
            HalfOpenSuccessThreshold = configuration.HalfOpenSuccessThreshold ?? defaults.HalfOpenSuccessThreshold,
            TimeProvider = defaults.TimeProvider
        };
    }

    private GrpcClientTlsOptions? BuildGrpcClientTlsOptions(GrpcClientTlsConfiguration configuration)
    {
        if (configuration is null)
        {
            return null;
        }

        var hasValues =
            HasCertificateMaterial(configuration.CertificatePath, configuration.CertificateData, configuration.CertificateDataSecret) ||
            !string.IsNullOrWhiteSpace(configuration.CertificatePassword) ||
            !string.IsNullOrWhiteSpace(configuration.CertificatePasswordSecret) ||
            configuration.AllowUntrustedCertificates.HasValue ||
            !string.IsNullOrWhiteSpace(configuration.TargetNameOverride);

        if (!hasValues)
        {
            return null;
        }

        var certificates = new X509Certificate2Collection();
        var clientCertificate = LoadCertificate(
            configuration.CertificatePath,
            configuration.CertificateData,
            configuration.CertificateDataSecret,
            configuration.CertificatePassword,
            configuration.CertificatePasswordSecret,
            "gRPC client TLS");
        if (clientCertificate is not null)
        {
            certificates.Add(clientCertificate);
        }

        if (!string.IsNullOrWhiteSpace(configuration.TargetNameOverride))
        {
            throw new OmniRelayConfigurationException("gRPC client target name override is not yet supported in configuration.");
        }

        RemoteCertificateValidationCallback? validationCallback = null;
        if (configuration.AllowUntrustedCertificates == true)
        {
#pragma warning disable CA5359 // Allow opt-in developer experience for untrusted clusters.
            validationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        return new GrpcClientTlsOptions
        {
            ClientCertificates = certificates,
            ServerCertificateValidationCallback = validationCallback
        };
    }

    private GrpcClientRuntimeOptions? BuildGrpcClientRuntimeOptions(GrpcClientRuntimeConfiguration configuration)
    {
        if (configuration is null)
        {
            return null;
        }

        var interceptors = ResolveClientInterceptors(configuration.Interceptors);
        var enableHttp3 = configuration.EnableHttp3 ?? false;
        var version = ParseHttpVersion(configuration.RequestVersion);
        var policy = ParseHttpVersionPolicy(configuration.VersionPolicy);

        var hasValues =
            enableHttp3 ||
            version is not null ||
            policy is not null ||
            configuration.MaxReceiveMessageSize.HasValue ||
            configuration.MaxSendMessageSize.HasValue ||
            configuration.KeepAlivePingDelay.HasValue ||
            configuration.KeepAlivePingTimeout.HasValue ||
            interceptors.Count > 0;

        if (!hasValues)
        {
            return null;
        }

        return new GrpcClientRuntimeOptions
        {
            EnableHttp3 = enableHttp3,
            RequestVersion = version,
            VersionPolicy = policy,
            MaxReceiveMessageSize = configuration.MaxReceiveMessageSize,
            MaxSendMessageSize = configuration.MaxSendMessageSize,
            KeepAlivePingDelay = configuration.KeepAlivePingDelay,
            KeepAlivePingTimeout = configuration.KeepAlivePingTimeout,
            Interceptors = interceptors
        };
    }

    private List<Interceptor> ResolveClientInterceptors(IEnumerable<string> typeNames)
    {
        var resolved = new List<Interceptor>();
        foreach (var typeName in typeNames)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                continue;
            }

            if (NativeAotTypeRegistry.TryResolveGrpcClientInterceptor(typeName, out var knownType))
            {
                var instance = (Interceptor)ActivatorUtilities.CreateInstance(_serviceProvider, knownType);
                resolved.Add(instance);
                continue;
            }

            if (_nativeAotStrict)
            {
                throw new OmniRelayConfigurationException(
                    $"gRPC client interceptor '{typeName}' is not registered for native AOT bootstrap. Add it to the registry or disable strict mode.");
            }

#pragma warning disable IL2026, IL3050
            var type = ResolveType(typeName);
#pragma warning restore IL2026, IL3050
            if (!typeof(Interceptor).IsAssignableFrom(type))
            {
                throw new OmniRelayConfigurationException(
                    $"Configured gRPC client interceptor '{typeName}' does not derive from {nameof(Interceptor)}.");
            }

#pragma warning disable IL2072
            var instanceFallback = (Interceptor)ActivatorUtilities.CreateInstance(_serviceProvider, type);
#pragma warning restore IL2072
            resolved.Add(instanceFallback);
        }

        return resolved;
    }

    private Action<WebApplication>? CreateHttpInboundAppConfigurator()
    {
        var actions = new List<Action<WebApplication>>();

        if (ShouldExposePrometheusMetrics())
        {
            var path = NormalizeScrapeEndpointPathForInbound(_options.Diagnostics?.OpenTelemetry?.Prometheus?.ScrapeEndpointPath);
            actions.Add(app => app.MapPrometheusScrapingEndpoint(path));
        }

        if (actions.Count == 0)
        {
            return null;
        }

        return app =>
        {
            foreach (var action in actions)
            {
                action(app);
            }
        };
    }

    private Action<IServiceCollection>? CreateHttpInboundServiceConfigurator()
    {
        var actions = new List<Action<IServiceCollection>>();

        if (ShouldExposePrometheusMetrics())
        {
            var scrapePath = NormalizeScrapeEndpointPathForInbound(_options.Diagnostics?.OpenTelemetry?.Prometheus?.ScrapeEndpointPath);
            var serviceName = string.IsNullOrWhiteSpace(_options.Service) ? "OmniRelay" : _options.Service!;
            actions.Add(services =>
            {
                services.AddOpenTelemetry()
                    .ConfigureResource(resource => resource.AddService(serviceName: serviceName, serviceInstanceId: serviceName))
                    .WithMetrics(builder =>
                    {
                        builder.AddPrometheusExporter(options => options.ScrapeEndpointPath = scrapePath);
                        builder.AddMeter("OmniRelay.Core.Diagnostics");
                        builder.AddMeter("OmniRelay.Transport.Http");
                        builder.AddMeter("OmniRelay.Transport.Grpc");
                    });
            });
        }

        var gossipAgent = _serviceProvider.GetService<IMeshGossipAgent>();
        if (gossipAgent is not null)
        {
            actions.Add(services => services.AddSingleton(gossipAgent));
        }

        if (actions.Count == 0)
        {
            return null;
        }

        return services =>
        {
            foreach (var action in actions)
            {
                action(services);
            }
        };
    }

    private static Action<IServiceCollection>? CreateGrpcInboundServiceConfigurator()
    {
        return null;
    }

    private static string NormalizeScrapeEndpointPathForInbound(string? path)
    {
        // Keep behavior in sync with ServiceCollectionExtensions.NormalizeScrapeEndpointPath
        // but localize it here for the inbound app.
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/omnirelay/metrics";
        }

        var normalized = path.Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        return normalized;
    }

    private static HttpServerRuntimeOptions? BuildHttpServerRuntimeOptions(HttpServerRuntimeConfiguration configuration)
    {
        if (configuration is null)
        {
            return null;
        }

        var options = new HttpServerRuntimeOptions();
        if (configuration.EnableHttp3 is { } enableHttp3)
        {
            options.EnableHttp3 = enableHttp3;
        }
        var http3Options = BuildHttp3RuntimeOptions(configuration.Http3);
        if (http3Options is not null)
        {
            options.Http3 = http3Options;
        }
        if (configuration.MaxRequestBodySize is { } max)
        {
            options.MaxRequestBodySize = max;
        }
        if (configuration.MaxInMemoryDecodeBytes is { } maxInMem)
        {
            options.MaxInMemoryDecodeBytes = maxInMem;
        }
        if (configuration.MaxRequestLineSize is { } maxRequestLine)
        {
            options.MaxRequestLineSize = maxRequestLine;
        }
        if (configuration.MaxRequestHeadersTotalSize is { } maxRequestHeaders)
        {
            options.MaxRequestHeadersTotalSize = maxRequestHeaders;
        }
        if (configuration.KeepAliveTimeout is { } keepAliveTimeout)
        {
            options.KeepAliveTimeout = keepAliveTimeout;
        }
        if (configuration.RequestHeadersTimeout is { } headersTimeout)
        {
            options.RequestHeadersTimeout = headersTimeout;
        }
        if (configuration.ServerStreamWriteTimeout is { } serverStreamWriteTimeout)
        {
            options.ServerStreamWriteTimeout = serverStreamWriteTimeout;
        }
        if (configuration.DuplexWriteTimeout is { } duplexWriteTimeout)
        {
            options.DuplexWriteTimeout = duplexWriteTimeout;
        }
        if (configuration.ServerStreamMaxMessageBytes is { } serverStreamMaxMessageBytes)
        {
            options.ServerStreamMaxMessageBytes = serverStreamMaxMessageBytes;
        }
        if (configuration.DuplexMaxFrameBytes is { } duplexMaxFrameBytes)
        {
            options.DuplexMaxFrameBytes = duplexMaxFrameBytes;
        }

        return (options.EnableHttp3 ||
                options.MaxRequestBodySize.HasValue ||
                options.MaxInMemoryDecodeBytes.HasValue ||
                options.MaxRequestLineSize.HasValue ||
                options.MaxRequestHeadersTotalSize.HasValue ||
                options.KeepAliveTimeout.HasValue ||
                options.RequestHeadersTimeout.HasValue ||
                options.ServerStreamWriteTimeout.HasValue ||
                options.DuplexWriteTimeout.HasValue ||
                options.ServerStreamMaxMessageBytes.HasValue ||
                options.DuplexMaxFrameBytes.HasValue ||
                options.Http3 is not null)
            ? options
            : null;
    }

    private static Http3RuntimeOptions? BuildHttp3RuntimeOptions(Http3ServerRuntimeConfiguration configuration)
    {
        if (configuration is null)
        {
            return null;
        }

        var hasValue =
            configuration.EnableAltSvc.HasValue ||
            configuration.IdleTimeout.HasValue ||
            configuration.KeepAliveInterval.HasValue ||
            configuration.MaxBidirectionalStreams.HasValue ||
            configuration.MaxUnidirectionalStreams.HasValue;

        if (!hasValue)
        {
            return null;
        }

        return new Http3RuntimeOptions
        {
            EnableAltSvc = configuration.EnableAltSvc,
            IdleTimeout = configuration.IdleTimeout,
            KeepAliveInterval = configuration.KeepAliveInterval,
            MaxBidirectionalStreams = configuration.MaxBidirectionalStreams,
            MaxUnidirectionalStreams = configuration.MaxUnidirectionalStreams
        };
    }

    private HttpServerTlsOptions? BuildHttpServerTlsOptions(HttpServerTlsConfiguration configuration)
    {
        if (configuration is null)
        {
            return null;
        }

        var certificate = LoadCertificate(
            configuration.CertificatePath,
            configuration.CertificateData,
            configuration.CertificateDataSecret,
            configuration.CertificatePassword,
            configuration.CertificatePasswordSecret,
            "HTTP server TLS");

        if (certificate is null)
        {
            return null;
        }

        var mode = ParseClientCertificateMode(configuration.ClientCertificateMode);

        return new HttpServerTlsOptions
        {
            Certificate = certificate,
            ClientCertificateMode = mode,
            CheckCertificateRevocation = configuration.CheckCertificateRevocation
        };
    }

    private static bool HasCertificateMaterial(string? path, string? data, string? dataSecret)
        => !string.IsNullOrWhiteSpace(path) || !string.IsNullOrWhiteSpace(data) || !string.IsNullOrWhiteSpace(dataSecret);

    private X509Certificate2? LoadCertificate(
        string? path,
        string? base64Data,
        string? base64DataSecret,
        string? password,
        string? passwordSecret,
        string context)
    {
        var resolvedPassword = password;
        if (string.IsNullOrWhiteSpace(resolvedPassword) && !string.IsNullOrWhiteSpace(passwordSecret))
        {
            using var secret = AcquireSecret(passwordSecret, $"{context} password");
            resolvedPassword = secret.AsString();
            if (string.IsNullOrEmpty(resolvedPassword))
            {
                throw new OmniRelayConfigurationException($"Secret '{passwordSecret}' referenced by {context} did not contain a password.");
            }
        }

        if (!string.IsNullOrWhiteSpace(base64Data))
        {
            var rawBytes = DecodeCertificateData(base64Data, context);
            return ImportCertificate(rawBytes, resolvedPassword);
        }

        if (!string.IsNullOrWhiteSpace(base64DataSecret))
        {
            using var secret = AcquireSecret(base64DataSecret, $"{context} certificate data");
            var text = secret.AsString();
            byte[] rawBytes;
            if (!string.IsNullOrWhiteSpace(text))
            {
                rawBytes = DecodeCertificateData(text!, context);
            }
            else
            {
                var memory = secret.AsMemory();
                if (memory.IsEmpty)
                {
                    throw new OmniRelayConfigurationException($"Secret '{base64DataSecret}' referenced by {context} was empty.");
                }

                rawBytes = memory.ToArray();
            }

            return ImportCertificate(rawBytes, resolvedPassword);
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            var resolved = ResolvePath(path);
            if (!File.Exists(resolved))
            {
                throw new OmniRelayConfigurationException($"{context} certificate '{resolved}' could not be found.");
            }

#pragma warning disable SYSLIB0057
            return string.IsNullOrEmpty(resolvedPassword)
                ? new X509Certificate2(resolved)
                : new X509Certificate2(resolved, resolvedPassword, X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
        }

        return null;
    }

    private void ConfigureBootstrapHost(DispatcherOptions dispatcherOptions)
    {
        if (!TryCreateBootstrapControlPlaneSettings(out var settings))
        {
            return;
        }

        var serverOptions = _serviceProvider.GetService<BootstrapServerOptions>();
        if (serverOptions is null)
        {
            throw new OmniRelayConfigurationException("Bootstrap hosting is enabled but security.bootstrap configuration was not bound.");
        }

        var loggerFactory = _serviceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        var host = new BootstrapControlPlaneHost(
            _serviceProvider,
            settings.HttpOptions,
            serverOptions,
            loggerFactory.CreateLogger<BootstrapControlPlaneHost>(),
            settings.TlsManager);
        dispatcherOptions.AddLifecycle("control-plane:bootstrap", host);
    }

    private static byte[] DecodeCertificateData(string data, string context)
    {
        try
        {
            return Convert.FromBase64String(data);
        }
        catch (FormatException ex)
        {
            throw new OmniRelayConfigurationException($"{context} certificateData is not valid Base64.", ex);
        }
    }

    private static X509Certificate2 ImportCertificate(byte[] rawBytes, string? password)
    {
        try
        {
#pragma warning disable SYSLIB0057
            return string.IsNullOrEmpty(password)
                ? new X509Certificate2(rawBytes)
                : new X509Certificate2(rawBytes, password, X509KeyStorageFlags.Exportable);
#pragma warning restore SYSLIB0057
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rawBytes);
        }
    }

    private SecretValue AcquireSecret(string name, string purpose)
    {
        if (_secretProvider is null)
        {
            throw new OmniRelayConfigurationException($"{purpose} references secret '{name}' but no secret provider was registered.");
        }

        var secret = _secretProvider.GetSecretSync(name);
        if (secret is null)
        {
            throw new OmniRelayConfigurationException($"Secret '{name}' referenced by {purpose} was not found.");
        }

        return secret;
    }

    private bool ShouldExposePrometheusMetrics()
    {
        var otel = _options.Diagnostics?.OpenTelemetry;
        if (otel is null)
        {
            return false;
        }

        var promEnabled = otel.Prometheus.Enabled ?? true;
        var otlpEnabled = otel.Otlp.Enabled ?? false;
        var metricsEnabled = otel.EnableMetrics ?? (promEnabled || otlpEnabled);
        var otelEnabled = otel.Enabled ?? metricsEnabled;

        return otelEnabled && metricsEnabled && promEnabled;
    }
    private GrpcServerRuntimeOptions? BuildGrpcServerRuntimeOptions(GrpcServerRuntimeConfiguration configuration)
    {
        var interceptors = ResolveServerInterceptorTypes(configuration.Interceptors);
        var enableHttp3 = configuration.EnableHttp3 ?? false;
        var http3Options = BuildHttp3RuntimeOptions(configuration.Http3);
        var hasValues =
            enableHttp3 ||
            configuration.MaxReceiveMessageSize.HasValue ||
            configuration.MaxSendMessageSize.HasValue ||
            configuration.EnableDetailedErrors.HasValue ||
            configuration.KeepAlivePingDelay.HasValue ||
            configuration.KeepAlivePingTimeout.HasValue ||
            configuration.ServerStreamWriteTimeout.HasValue ||
            configuration.DuplexWriteTimeout.HasValue ||
            configuration.ServerStreamMaxMessageBytes.HasValue ||
            configuration.DuplexMaxMessageBytes.HasValue ||
            interceptors.Count > 0 ||
            http3Options is not null;

        if (!hasValues)
        {
            return null;
        }

        return new GrpcServerRuntimeOptions
        {
            EnableHttp3 = enableHttp3,
            MaxReceiveMessageSize = configuration.MaxReceiveMessageSize,
            MaxSendMessageSize = configuration.MaxSendMessageSize,
            KeepAlivePingDelay = configuration.KeepAlivePingDelay,
            KeepAlivePingTimeout = configuration.KeepAlivePingTimeout,
            EnableDetailedErrors = configuration.EnableDetailedErrors,
            Interceptors = interceptors,
            ServerStreamWriteTimeout = configuration.ServerStreamWriteTimeout,
            DuplexWriteTimeout = configuration.DuplexWriteTimeout,
            ServerStreamMaxMessageBytes = configuration.ServerStreamMaxMessageBytes,
            DuplexMaxMessageBytes = configuration.DuplexMaxMessageBytes,
            Http3 = http3Options
        };
    }

    private List<Type> ResolveServerInterceptorTypes(IEnumerable<string> typeNames)
    {
        var resolved = new List<Type>();
        foreach (var typeName in typeNames)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                continue;
            }

            if (NativeAotTypeRegistry.TryResolveGrpcServerInterceptor(typeName, out var knownType))
            {
                resolved.Add(knownType);
                continue;
            }

            if (_nativeAotStrict)
            {
                throw new OmniRelayConfigurationException(
                    $"gRPC server interceptor '{typeName}' is not registered for native AOT bootstrap. Add it to the registry or disable strict mode.");
            }

#pragma warning disable IL2026, IL3050
            var type = ResolveType(typeName);
#pragma warning restore IL2026, IL3050
            if (!typeof(Interceptor).IsAssignableFrom(type))
            {
                throw new OmniRelayConfigurationException(
                    $"Configured gRPC server interceptor '{typeName}' does not derive from {nameof(Interceptor)}.");
            }

            resolved.Add(type);
        }

        return resolved;
    }

    private GrpcServerTlsOptions? BuildGrpcServerTlsOptions(GrpcServerTlsConfiguration configuration)
    {
        if (configuration is null)
        {
            return null;
        }

        var certificate = LoadCertificate(
            configuration.CertificatePath,
            configuration.CertificateData,
            configuration.CertificateDataSecret,
            configuration.CertificatePassword,
            configuration.CertificatePasswordSecret,
            "gRPC server TLS");

        if (certificate is null)
        {
            return null;
        }

        var mode = ParseClientCertificateMode(configuration.ClientCertificateMode);

        return new GrpcServerTlsOptions
        {
            Certificate = certificate,
            ClientCertificateMode = mode,
            CheckCertificateRevocation = configuration.CheckCertificateRevocation
        };
    }

    private GrpcTelemetryOptions? BuildGrpcTelemetryOptions(GrpcTelemetryConfiguration configuration, bool serverSide)
    {
        var loggerFactory = _serviceProvider.GetService<ILoggerFactory>();
        var hasValues = configuration.EnableClientLogging.HasValue || configuration.EnableServerLogging.HasValue || loggerFactory is not null;

        if (!hasValues)
        {
            return null;
        }

        return new GrpcTelemetryOptions
        {
            EnableClientLogging = configuration.EnableClientLogging ?? !serverSide,
            EnableServerLogging = configuration.EnableServerLogging ?? serverSide,
            LoggerFactory = loggerFactory
        };
    }

    private void ValidateNativeEncodings()
    {
        var json = _options.Encodings.Json;
        if (json.Inbound.Count > 0 || json.Outbound.Count > 0 || json.Profiles.Count > 0)
        {
            throw new OmniRelayConfigurationException(
                "Configuration-based JSON codec registrations are not supported in native AOT mode. " +
                "Register codecs programmatically or generate source-based codecs instead.");
        }
    }

    [RequiresDynamicCode("JSON codec registration relies on reflection.")]
    [RequiresUnreferencedCode("JSON codec registration relies on reflection.")]
    private void ConfigureEncodings(DispatcherOptions dispatcherOptions) => ConfigureJsonEncodings(dispatcherOptions);

    private void ConfigureMeshComponents(DispatcherOptions dispatcherOptions)
    {
        var gossipAdded = false;
        var gossipAgent = _serviceProvider.GetService<IMeshGossipAgent>();
        if (gossipAgent is not null && gossipAgent.IsEnabled)
        {
            dispatcherOptions.AddLifecycle("mesh-gossip", gossipAgent);
            gossipAdded = true;
        }

        var leadershipAdded = false;
        var leadershipCoordinator = _serviceProvider.GetService<LeadershipCoordinator>();
        var leadershipOptions = _serviceProvider.GetService<IOptions<LeadershipOptions>>()?.Value;
        if (leadershipCoordinator is not null && leadershipOptions?.Enabled == true)
        {
            dispatcherOptions.AddLifecycle(
                "mesh-leadership",
                leadershipCoordinator,
                gossipAdded ? MeshGossipDependency : null);
            leadershipAdded = true;
        }

        ConfigureControlPlaneHosts(dispatcherOptions, gossipAdded, leadershipAdded);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Diagnostics control-plane host intentionally uses reflection for service wiring.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Diagnostics control-plane host intentionally uses reflection for service wiring.")]
    private void ConfigureControlPlaneHosts(DispatcherOptions dispatcherOptions, bool gossipAdded, bool leadershipAdded)
    {
        if (!TryCreateDiagnosticsControlPlaneSettings(out var settings))
        {
            return;
        }

        if (settings.HttpTlsManager is not null)
        {
            settings.HttpOptions.SharedTls = settings.HttpTlsManager;
        }

        if (settings.GrpcOptions is not null && settings.GrpcTlsManager is not null)
        {
            settings.GrpcOptions.SharedTls = settings.GrpcTlsManager;
        }

        var loggerFactory = _serviceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        var httpHost = new DiagnosticsControlPlaneHost(
            _serviceProvider,
            settings.HttpOptions,
            settings.EnableLoggingToggle,
            settings.EnableSamplingToggle,
            settings.EnableLeaseHealthDiagnostics,
            settings.EnablePeerDiagnostics,
            settings.EnableLeadershipDiagnostics,
            settings.EnableDocumentation,
            settings.EnableProbeDiagnostics,
            settings.EnableChaosControl,
            settings.EnableShardDiagnostics,
            loggerFactory.CreateLogger<DiagnosticsControlPlaneHost>(),
            settings.HttpTlsManager);

        var httpDependencies = new List<string>();
        if (gossipAdded)
        {
            httpDependencies.Add("mesh-gossip");
        }

        if (leadershipAdded && settings.EnableLeadershipDiagnostics)
        {
            httpDependencies.Add("mesh-leadership");
        }

        dispatcherOptions.AddLifecycle(
            "control-plane:http",
            httpHost,
            httpDependencies.Count == 0 ? null : httpDependencies);

        if (settings.GrpcOptions is not null)
        {
            var grpcHost = new LeadershipControlPlaneHost(
                _serviceProvider,
                settings.GrpcOptions,
                loggerFactory.CreateLogger<LeadershipControlPlaneHost>(),
                settings.GrpcTlsManager);
            dispatcherOptions.AddLifecycle(
                "control-plane:grpc",
                grpcHost,
                leadershipAdded ? MeshLeadershipDependency : null);
        }

        ConfigureBootstrapHost(dispatcherOptions);
    }

    private void RegisterDrainParticipant(string name, ILifecycle lifecycle)
    {
        if (lifecycle is not INodeDrainParticipant participant)
        {
            return;
        }

        var coordinator = _serviceProvider.GetService<NodeDrainCoordinator>();
        coordinator?.RegisterParticipant(name, participant);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Diagnostics control-plane host intentionally uses reflection for service wiring.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Diagnostics control-plane host intentionally uses reflection for service wiring.")]
    private bool TryCreateDiagnosticsControlPlaneSettings([NotNullWhen(true)] out DiagnosticsControlPlaneSettings? settings)
    {
        var diagnostics = _options.Diagnostics;
        if (diagnostics is null)
        {
            settings = default;
            return false;
        }

        var runtime = diagnostics.Runtime ?? new RuntimeDiagnosticsConfiguration();
        var enableLogging = runtime.EnableLoggingLevelToggle ?? false;
        var enableSampling = runtime.EnableTraceSamplingToggle ?? false;

        var peerHealthProviders = _serviceProvider.GetServices<IPeerHealthSnapshotProvider>()
            .Where(static provider => provider is not null)
            .ToArray();
        var enableLeaseHealth = peerHealthProviders.Length > 0;

        var gossipAgent = _serviceProvider.GetService<IMeshGossipAgent>();
        var enablePeerDiagnostics = gossipAgent is not null && gossipAgent.IsEnabled;

        var leadershipObserver = _serviceProvider.GetService<ILeadershipObserver>();
        var enableLeadershipDiagnostics = leadershipObserver is not null;

        var documentation = diagnostics.Documentation ?? new DocumentationDiagnosticsConfiguration();
        var enableDocumentation = (documentation.EnableOpenApi ?? false) || (documentation.EnableGrpcReflection ?? false);

        var probes = diagnostics.Probes ?? new ProbesDiagnosticsConfiguration();
        var enableProbeDiagnostics = probes.EnableDiagnosticsEndpoint ?? false;

        var chaos = diagnostics.Chaos ?? new ChaosDiagnosticsConfiguration();
        var enableChaosControl = chaos.EnableControlEndpoint ?? false;

        var shardService = _serviceProvider.GetService<ShardControlPlaneService>();
        var enableShardDiagnostics = shardService is not null;

        var enableControlPlane = runtime.EnableControlPlane ?? (enableLogging || enableSampling || enableLeaseHealth || enablePeerDiagnostics || enableLeadershipDiagnostics || enableDocumentation || enableProbeDiagnostics || enableChaosControl || enableShardDiagnostics);
        if (!enableControlPlane)
        {
            settings = default;
            return false;
        }

        var controlPlane = diagnostics.ControlPlane ?? new DiagnosticsControlPlaneConfiguration();
        var httpOptions = BuildHttpControlPlaneHostOptions(controlPlane);
        if (httpOptions.Urls.Count == 0)
        {
            settings = default;
            return false;
        }

        var httpTlsManager = CreateTransportTlsManager(controlPlane.Tls, "diagnostics HTTP");

        GrpcControlPlaneHostOptions? grpcOptions = null;
        TransportTlsManager? grpcTlsManager = null;
        if (controlPlane.GrpcUrls.Count > 0 && enableLeadershipDiagnostics)
        {
            grpcOptions = BuildGrpcControlPlaneHostOptions(controlPlane);
            grpcTlsManager = CreateTransportTlsManager(controlPlane.Tls, "diagnostics gRPC");
        }

        settings = new DiagnosticsControlPlaneSettings(
            httpOptions,
            grpcOptions,
            httpTlsManager,
            grpcTlsManager,
            enableLogging,
            enableSampling,
            enableLeaseHealth,
            enablePeerDiagnostics,
            enableLeadershipDiagnostics,
            enableDocumentation,
            enableProbeDiagnostics,
            enableChaosControl,
            enableShardDiagnostics);
        return true;
    }

    private bool TryCreateBootstrapControlPlaneSettings([NotNullWhen(true)] out BootstrapControlPlaneSettings? settings)
    {
        var bootstrap = _options.Security?.Bootstrap;
        if (bootstrap?.Enabled != true)
        {
            settings = default;
            return false;
        }

        if (bootstrap.HttpUrls.Count == 0)
        {
            settings = default;
            return false;
        }

        var httpOptions = new HttpControlPlaneHostOptions();
        foreach (var url in bootstrap.HttpUrls)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            httpOptions.Urls.Add(url);
        }

        if (httpOptions.Urls.Count == 0)
        {
            settings = default;
            return false;
        }

        var tlsManager = CreateTransportTlsManager(bootstrap.Tls, "bootstrap HTTP");
        if (tlsManager is not null)
        {
            httpOptions.SharedTls = tlsManager;
        }

        settings = new BootstrapControlPlaneSettings(httpOptions, tlsManager);
        return true;
    }

    private HttpControlPlaneHostOptions BuildHttpControlPlaneHostOptions(DiagnosticsControlPlaneConfiguration configuration)
    {
        var options = new HttpControlPlaneHostOptions();
        var hasCustomUrls = false;
        foreach (var url in configuration.HttpUrls)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (!hasCustomUrls)
            {
                options.Urls.Clear();
                hasCustomUrls = true;
            }

            options.Urls.Add(url);
        }

        options.Runtime = BuildHttpServerRuntimeOptions(configuration.HttpRuntime);
        options.Tls = BuildHttpServerTlsOptions(configuration.HttpTls);
        options.ClientCertificateMode = ParseClientCertificateMode(configuration.HttpTls.ClientCertificateMode);
        options.CheckCertificateRevocation = configuration.HttpTls.CheckCertificateRevocation;
        return options;
    }

    [RequiresDynamicCode("Server interceptors are instantiated via dependency injection.")]
    [RequiresUnreferencedCode("Server interceptors are instantiated via dependency injection.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Diagnostics control-plane host intentionally uses reflection for service wiring.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Diagnostics control-plane host intentionally uses reflection for service wiring.")]
    private GrpcControlPlaneHostOptions BuildGrpcControlPlaneHostOptions(DiagnosticsControlPlaneConfiguration configuration)
    {
        var options = new GrpcControlPlaneHostOptions();
        var hasCustomUrls = false;
        foreach (var url in configuration.GrpcUrls)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (!hasCustomUrls)
            {
                options.Urls.Clear();
                hasCustomUrls = true;
            }

            options.Urls.Add(url);
        }

        options.Runtime = BuildGrpcServerRuntimeOptions(configuration.GrpcRuntime);
        options.Tls = BuildGrpcServerTlsOptions(configuration.GrpcTls);
        options.ClientCertificateMode = ParseClientCertificateMode(configuration.GrpcTls.ClientCertificateMode);
        options.CheckCertificateRevocation = configuration.GrpcTls.CheckCertificateRevocation;
        options.Telemetry = new GrpcTelemetryOptions
        {
            EnableServerLogging = true,
            LoggerFactory = _serviceProvider.GetService<ILoggerFactory>()
        };
        return options;
    }

    private TransportTlsManager? CreateTransportTlsManager(TransportTlsConfiguration configuration, string purpose)
    {
        if (configuration is null)
        {
            return null;
        }

        var hasCertificate = !string.IsNullOrWhiteSpace(configuration.CertificatePath) ||
            !string.IsNullOrWhiteSpace(configuration.CertificateData);

        if (!hasCertificate)
        {
            return null;
        }

        var options = new TransportTlsOptions
        {
            CertificatePath = configuration.CertificatePath,
            CertificateData = configuration.CertificateData,
            CertificateDataSecret = configuration.CertificateDataSecret,
            CertificatePassword = configuration.CertificatePassword,
            CertificatePasswordSecret = configuration.CertificatePasswordSecret,
            AllowUntrustedCertificates = configuration.AllowUntrustedCertificates ?? false,
            CheckCertificateRevocation = configuration.CheckCertificateRevocation ?? true
        };

        if (!string.IsNullOrWhiteSpace(configuration.ReloadInterval))
        {
            if (TimeSpan.TryParse(configuration.ReloadInterval, CultureInfo.InvariantCulture, out var parsed))
            {
                options.ReloadInterval = parsed;
            }
            else
            {
                throw new OmniRelayConfigurationException($"Invalid TLS reload interval '{configuration.ReloadInterval}' for {purpose}.");
            }
        }

        foreach (var thumbprint in configuration.AllowedThumbprints)
        {
            if (string.IsNullOrWhiteSpace(thumbprint))
            {
                continue;
            }

            options.AllowedThumbprints.Add(thumbprint.Trim());
        }

        var loggerFactory = _serviceProvider.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        return new TransportTlsManager(options, loggerFactory.CreateLogger<TransportTlsManager>(), _secretProvider);
    }

    private sealed record DiagnosticsControlPlaneSettings(
        HttpControlPlaneHostOptions HttpOptions,
        GrpcControlPlaneHostOptions? GrpcOptions,
        TransportTlsManager? HttpTlsManager,
        TransportTlsManager? GrpcTlsManager,
        bool EnableLoggingToggle,
        bool EnableSamplingToggle,
        bool EnableLeaseHealthDiagnostics,
        bool EnablePeerDiagnostics,
        bool EnableLeadershipDiagnostics,
        bool EnableDocumentation,
        bool EnableProbeDiagnostics,
        bool EnableChaosControl,
        bool EnableShardDiagnostics);

    private sealed record BootstrapControlPlaneSettings(
        HttpControlPlaneHostOptions HttpOptions,
        TransportTlsManager? TlsManager);

    [RequiresDynamicCode("JSON codec registration relies on reflection.")]
    [RequiresUnreferencedCode("JSON codec registration relies on reflection.")]
    private void ConfigureJsonEncodings(DispatcherOptions dispatcherOptions)
    {
        var jsonConfig = _options.Encodings?.Json;
        if (jsonConfig is null)
        {
            return;
        }

        if (jsonConfig.Inbound.Count == 0 && jsonConfig.Outbound.Count == 0)
        {
            return;
        }

        var basePath = ResolveContentRootPath();
        var profiles = BuildJsonProfiles(jsonConfig);

        foreach (var registration in jsonConfig.Inbound)
        {
            RegisterJsonCodec(dispatcherOptions, registration, profiles, ProcedureCodecScope.Inbound, basePath);
        }

        foreach (var registration in jsonConfig.Outbound)
        {
            RegisterJsonCodec(dispatcherOptions, registration, profiles, ProcedureCodecScope.Outbound, basePath);
        }
    }

    private static Dictionary<string, JsonProfileDescriptor> BuildJsonProfiles(JsonEncodingConfiguration configuration)
    {
        var profiles = new Dictionary<string, JsonProfileDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in configuration.Profiles)
        {
            profiles[entry.Key] = new JsonProfileDescriptor(
                entry.Value.Options,
                entry.Value.Converters,
                entry.Value.Context);
        }

        return profiles;
    }

    [RequiresDynamicCode("JSON codec registration relies on reflection.")]
    [RequiresUnreferencedCode("JSON codec registration relies on reflection.")]
    private static void RegisterJsonCodec(
        DispatcherOptions dispatcherOptions,
        JsonCodecRegistrationConfiguration registration,
        IDictionary<string, JsonProfileDescriptor> profiles,
        ProcedureCodecScope scope,
        string basePath)
    {
        var procedure = registration.Procedure?.Trim();
        if (string.IsNullOrWhiteSpace(procedure))
        {
            throw new OmniRelayConfigurationException("JSON codec registrations must specify a procedure name.");
        }

        var kind = ParseProcedureKind(registration.Kind, procedure);

        var requestType = ResolveType(registration.RequestType, $"json codec registration for '{procedure}'");
        var responseType = ResolveResponseType(registration, kind, procedure);

        var materials = BuildJsonCodecMaterials(profiles, registration, requestType);

        var encoding = string.IsNullOrWhiteSpace(registration.Encoding)
            ? "application/json"
            : registration.Encoding!.Trim();

        var requestSchema = LoadJsonSchema(registration.Schemas.Request, basePath, $"request schema for '{procedure}'");
        var responseSchema = LoadJsonSchema(registration.Schemas.Response, basePath, $"response schema for '{procedure}'");

        var codecInstance = CreateJsonCodecInstance(
            requestType,
            responseType,
            materials.Options,
            encoding,
            materials.Context,
            requestSchema,
            registration.Schemas.Request,
            responseSchema,
            registration.Schemas.Response);

        RegisterCodecWithDispatcher(
            dispatcherOptions,
            scope,
            registration.Service,
            procedure,
            kind,
            requestType,
            responseType,
            codecInstance,
            registration.Aliases);
    }

    [RequiresDynamicCode("JSON serializer contexts and converters rely on reflection.")]
    [RequiresUnreferencedCode("JSON serializer contexts and converters rely on reflection.")]
    private static (JsonSerializerOptions Options, JsonSerializerContext? Context) BuildJsonCodecMaterials(
        IDictionary<string, JsonProfileDescriptor> profiles,
        JsonCodecRegistrationConfiguration registration,
        Type requestType)
    {
        JsonSerializerOptions options = CreateDefaultJsonSerializerOptions();
        JsonProfileDescriptor? profile = null;

        if (!string.IsNullOrWhiteSpace(registration.Profile))
        {
            if (!profiles.TryGetValue(registration.Profile!, out profile))
            {
                throw new OmniRelayConfigurationException($"No JSON codec profile named '{registration.Profile}' was found.");
            }

            options = new JsonSerializerOptions(options);
            ApplyJsonSerializerOptions(options, profile.Options, profile.Converters);
        }

        options = new JsonSerializerOptions(options);
        ApplyJsonSerializerOptions(options, registration.Options, registration.Options.Converters);

        var contextTypeName = registration.Context ?? profile?.ContextTypeName;
        var context = CreateSerializerContext(contextTypeName, options, registration.Procedure ?? requestType.FullName ?? "json-procedure");

        return (options, context);
    }

    [RequiresDynamicCode("JsonSerializerContext types are instantiated via reflection.")]
    [RequiresUnreferencedCode("JsonSerializerContext types are instantiated via reflection.")]
    private static JsonSerializerContext? CreateSerializerContext(string? contextTypeName, JsonSerializerOptions options, string procedure)
    {
        if (string.IsNullOrWhiteSpace(contextTypeName))
        {
            return null;
        }

        var contextType = ResolveType(contextTypeName, $"JsonSerializerContext for '{procedure}'");
        if (!typeof(JsonSerializerContext).IsAssignableFrom(contextType))
        {
            throw new OmniRelayConfigurationException($"Type '{contextTypeName}' is not a valid JsonSerializerContext.");
        }

        try
        {
            var ctorWithOptions = contextType.GetConstructor([typeof(JsonSerializerOptions)]);
            if (ctorWithOptions is not null)
            {
                var instance = ctorWithOptions.Invoke([options]);
                if (instance is JsonSerializerContext context)
                {
                    return context;
                }
            }

            var defaultInstance = Activator.CreateInstance(contextType);
            return defaultInstance as JsonSerializerContext
                ?? throw new OmniRelayConfigurationException($"Failed to instantiate JsonSerializerContext '{contextTypeName}'.");
        }
        catch (OmniRelayConfigurationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OmniRelayConfigurationException($"Failed to instantiate JsonSerializerContext '{contextTypeName}'.", ex);
        }
    }

    private static JsonSchema? LoadJsonSchema(string? path, string basePath, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var resolved = Path.IsPathRooted(path)
            ? path
            : Path.Combine(basePath, path);

        if (!File.Exists(resolved))
        {
            throw new OmniRelayConfigurationException($"Unable to locate JSON schema '{path}' for {description}.");
        }

        try
        {
            var schemaText = File.ReadAllText(resolved);
            return JsonSchema.FromText(schemaText);
        }
        catch (Exception ex)
        {
            throw new OmniRelayConfigurationException($"Failed to load JSON schema '{path}' for {description}.", ex);
        }
    }

    private static ProcedureKind ParseProcedureKind(string? value, string procedure)
    {
        if (Enum.TryParse<ProcedureKind>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new OmniRelayConfigurationException($"JSON codec registration for '{procedure}' specifies invalid procedure kind '{value}'.");
    }

    [RequiresDynamicCode("Type resolution relies on runtime reflection.")]
    [RequiresUnreferencedCode("Type resolution relies on runtime reflection.")]
    private static Type ResolveType(string? typeName, string description)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new OmniRelayConfigurationException($"Type name is required for {description}.");
        }

        var type = Type.GetType(typeName, throwOnError: false);
        if (type is null)
        {
            throw new OmniRelayConfigurationException($"Unable to resolve type '{typeName}' for {description}.");
        }

        return type;
    }

    [RequiresDynamicCode("Response type resolution uses reflection.")]
    [RequiresUnreferencedCode("Response type resolution uses reflection.")]
    private static Type ResolveResponseType(JsonCodecRegistrationConfiguration registration, ProcedureKind kind, string procedure)
    {
        if (kind == ProcedureKind.Oneway)
        {
            return typeof(object);
        }

        if (string.IsNullOrWhiteSpace(registration.ResponseType))
        {
            throw new OmniRelayConfigurationException($"JSON codec registration for '{procedure}' must specify a response type.");
        }

        return ResolveType(registration.ResponseType, $"json codec registration for '{procedure}'");
    }

    [RequiresDynamicCode("Json codecs are instantiated via generic reflection.")]
    [RequiresUnreferencedCode("Json codecs are instantiated via generic reflection.")]
    private static object CreateJsonCodecInstance(
        Type requestType,
        Type responseType,
        JsonSerializerOptions options,
        string encoding,
        JsonSerializerContext? context,
        JsonSchema? requestSchema,
        string? requestSchemaId,
        JsonSchema? responseSchema,
        string? responseSchemaId)
    {
        var codecType = typeof(JsonCodec<,>).MakeGenericType(requestType, responseType);

        try
        {
            return Activator.CreateInstance(
                       codecType,
                       options,
                       encoding,
                       context,
                       requestSchema,
                       requestSchemaId,
                       responseSchema,
                       responseSchemaId)
                   ?? throw new OmniRelayConfigurationException($"Failed to create JsonCodec instance for '{requestType.FullName}' → '{responseType.FullName}'.");
        }
        catch (TargetInvocationException ex)
        {
            throw new OmniRelayConfigurationException("Failed to create JsonCodec instance.", ex.InnerException ?? ex);
        }
        catch (Exception ex)
        {
            throw new OmniRelayConfigurationException("Failed to create JsonCodec instance.", ex);
        }
    }

    [RequiresDynamicCode("Dispatcher codec registration uses reflection.")]
    [RequiresUnreferencedCode("Dispatcher codec registration uses reflection.")]
    private static void RegisterCodecWithDispatcher(
        DispatcherOptions dispatcherOptions,
        ProcedureCodecScope scope,
        string? service,
        string procedure,
        ProcedureKind kind,
        Type requestType,
        Type responseType,
        object codec,
        IList<string> aliases)
    {
        var aliasArgument = aliases.Count == 0 ? null : aliases;

        try
        {
            switch (scope)
            {
                case ProcedureCodecScope.Inbound:
                    RegisterInboundCodec(dispatcherOptions, kind, requestType, responseType, codec, procedure, aliasArgument);
                    break;
                case ProcedureCodecScope.Outbound:
                    if (string.IsNullOrWhiteSpace(service))
                    {
                        throw new OmniRelayConfigurationException($"JSON codec registration for '{procedure}' must specify a service when used for outbound codecs.");
                    }

                    RegisterOutboundCodec(dispatcherOptions, kind, requestType, responseType, codec, service!, procedure, aliasArgument);
                    break;
                default:
                    throw new OmniRelayConfigurationException($"Unsupported codec scope '{scope}'.");
            }
        }
        catch (TargetInvocationException ex)
        {
            throw new OmniRelayConfigurationException("Failed to register JSON codec with dispatcher.", ex.InnerException ?? ex);
        }
    }

    [RequiresDynamicCode("Dispatcher codec registration uses reflection.")]
    [RequiresUnreferencedCode("Dispatcher codec registration uses reflection.")]
    private static void RegisterInboundCodec(
        DispatcherOptions dispatcherOptions,
        ProcedureKind kind,
        Type requestType,
        Type responseType,
        object codec,
        string procedure,
        IList<string>? aliases)
    {
        switch (kind)
        {
            case ProcedureKind.Unary:
                InvokeCodecRegistration(dispatcherOptions, nameof(DispatcherOptions.AddInboundUnaryCodec), requestType, responseType, codec, procedure, aliases);
                break;
            case ProcedureKind.Oneway:
                InvokeOnewayCodecRegistration(dispatcherOptions, nameof(DispatcherOptions.AddInboundOnewayCodec), requestType, codec, procedure, aliases);
                break;
            case ProcedureKind.Stream:
                InvokeCodecRegistration(dispatcherOptions, nameof(DispatcherOptions.AddInboundStreamCodec), requestType, responseType, codec, procedure, aliases);
                break;
            case ProcedureKind.ClientStream:
                InvokeCodecRegistration(dispatcherOptions, nameof(DispatcherOptions.AddInboundClientStreamCodec), requestType, responseType, codec, procedure, aliases);
                break;
            case ProcedureKind.Duplex:
                InvokeCodecRegistration(dispatcherOptions, nameof(DispatcherOptions.AddInboundDuplexCodec), requestType, responseType, codec, procedure, aliases);
                break;
            default:
                throw new OmniRelayConfigurationException($"JSON codec registrations do not support inbound kind '{kind}'.");
        }
    }

    [RequiresDynamicCode("Dispatcher codec registration uses reflection.")]
    [RequiresUnreferencedCode("Dispatcher codec registration uses reflection.")]
    private static void RegisterOutboundCodec(
        DispatcherOptions dispatcherOptions,
        ProcedureKind kind,
        Type requestType,
        Type responseType,
        object codec,
        string service,
        string procedure,
        IList<string>? aliases)
    {
        switch (kind)
        {
            case ProcedureKind.Unary:
                InvokeCodecRegistration(dispatcherOptions, nameof(DispatcherOptions.AddOutboundUnaryCodec), requestType, responseType, codec, service, procedure, aliases);
                break;
            case ProcedureKind.Oneway:
                InvokeOnewayCodecRegistration(dispatcherOptions, nameof(DispatcherOptions.AddOutboundOnewayCodec), requestType, codec, service, procedure, aliases);
                break;
            case ProcedureKind.Stream:
                InvokeCodecRegistration(dispatcherOptions, nameof(DispatcherOptions.AddOutboundStreamCodec), requestType, responseType, codec, service, procedure, aliases);
                break;
            case ProcedureKind.ClientStream:
                InvokeCodecRegistration(dispatcherOptions, nameof(DispatcherOptions.AddOutboundClientStreamCodec), requestType, responseType, codec, service, procedure, aliases);
                break;
            case ProcedureKind.Duplex:
                InvokeCodecRegistration(dispatcherOptions, nameof(DispatcherOptions.AddOutboundDuplexCodec), requestType, responseType, codec, service, procedure, aliases);
                break;
            default:
                throw new OmniRelayConfigurationException($"JSON codec registrations do not support outbound kind '{kind}'.");
        }
    }

    [RequiresDynamicCode("Dispatcher option registration uses generic reflection.")]
    [RequiresUnreferencedCode("Dispatcher option registration uses generic reflection.")]
    private static void InvokeCodecRegistration(
        DispatcherOptions dispatcherOptions,
        string methodName,
        Type requestType,
        Type responseType,
        object codec,
        string procedure,
        IList<string>? aliases)
    {
        var method = typeof(DispatcherOptions).GetMethod(methodName) ??
                     throw new InvalidOperationException($"DispatcherOptions.{methodName} not found.");

        var generic = method.MakeGenericMethod(requestType, responseType);
        generic.Invoke(dispatcherOptions, [procedure, codec, aliases]);
    }

    [RequiresDynamicCode("Dispatcher option registration uses generic reflection.")]
    [RequiresUnreferencedCode("Dispatcher option registration uses generic reflection.")]
    private static void InvokeCodecRegistration(
        DispatcherOptions dispatcherOptions,
        string methodName,
        Type requestType,
        Type responseType,
        object codec,
        string service,
        string procedure,
        IList<string>? aliases)
    {
        var method = typeof(DispatcherOptions).GetMethod(methodName) ??
                     throw new InvalidOperationException($"DispatcherOptions.{methodName} not found.");

        var generic = method.MakeGenericMethod(requestType, responseType);
        generic.Invoke(dispatcherOptions, [service, procedure, codec, aliases]);
    }

    [RequiresDynamicCode("Dispatcher option registration uses generic reflection.")]
    [RequiresUnreferencedCode("Dispatcher option registration uses generic reflection.")]
    private static void InvokeOnewayCodecRegistration(
        DispatcherOptions dispatcherOptions,
        string methodName,
        Type requestType,
        object codec,
        string procedure,
        IList<string>? aliases)
    {
        var method = typeof(DispatcherOptions).GetMethod(methodName) ??
                     throw new InvalidOperationException($"DispatcherOptions.{methodName} not found.");

        var generic = method.MakeGenericMethod(requestType);
        generic.Invoke(dispatcherOptions, [procedure, codec, aliases]);
    }

    [RequiresDynamicCode("Dispatcher option registration uses generic reflection.")]
    [RequiresUnreferencedCode("Dispatcher option registration uses generic reflection.")]
    private static void InvokeOnewayCodecRegistration(
        DispatcherOptions dispatcherOptions,
        string methodName,
        Type requestType,
        object codec,
        string service,
        string procedure,
        IList<string>? aliases)
    {
        var method = typeof(DispatcherOptions).GetMethod(methodName) ??
                     throw new InvalidOperationException($"DispatcherOptions.{methodName} not found.");

        var generic = method.MakeGenericMethod(requestType);
        generic.Invoke(dispatcherOptions, [service, procedure, codec, aliases]);
    }

    private static JsonSerializerOptions CreateDefaultJsonSerializerOptions() =>
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

    [RequiresDynamicCode("JSON converters are instantiated via reflection.")]
    [RequiresUnreferencedCode("JSON converters are instantiated via reflection.")]
    private static void ApplyJsonSerializerOptions(
        JsonSerializerOptions target,
        JsonSerializerOptionsConfiguration configuration,
        IList<string> additionalConverters)
    {
        if (configuration.PropertyNameCaseInsensitive.HasValue)
        {
            target.PropertyNameCaseInsensitive = configuration.PropertyNameCaseInsensitive.Value;
        }

        if (configuration.WriteIndented.HasValue)
        {
            target.WriteIndented = configuration.WriteIndented.Value;
        }

        if (configuration.AllowTrailingCommas.HasValue)
        {
            target.AllowTrailingCommas = configuration.AllowTrailingCommas.Value;
        }

        if (configuration.ReadCommentHandling.HasValue)
        {
            target.ReadCommentHandling = configuration.ReadCommentHandling.Value
                ? JsonCommentHandling.Allow
                : JsonCommentHandling.Disallow;
        }

        if (configuration.IgnoreNullValues.HasValue)
        {
            target.DefaultIgnoreCondition = configuration.IgnoreNullValues.Value
                ? JsonIgnoreCondition.WhenWritingNull
                : JsonIgnoreCondition.Never;
        }

        if (!string.IsNullOrWhiteSpace(configuration.DefaultIgnoreCondition))
        {
            if (Enum.TryParse<JsonIgnoreCondition>(configuration.DefaultIgnoreCondition, ignoreCase: true, out var ignoreCondition))
            {
                target.DefaultIgnoreCondition = ignoreCondition;
            }
            else
            {
                throw new OmniRelayConfigurationException($"Unsupported JSON ignore condition '{configuration.DefaultIgnoreCondition}'.");
            }
        }

        if (configuration.NumberHandling.Count > 0)
        {
            JsonNumberHandling handling = 0;
            foreach (var entry in configuration.NumberHandling)
            {
                if (!Enum.TryParse<JsonNumberHandling>(entry, ignoreCase: true, out var parsed))
                {
                    throw new OmniRelayConfigurationException($"Unsupported JSON number handling flag '{entry}'.");
                }

                handling |= parsed;
            }

            target.NumberHandling = handling;
        }

        if (!string.IsNullOrWhiteSpace(configuration.PropertyNamingPolicy))
        {
            var policy = configuration.PropertyNamingPolicy.Trim();
            if (policy.Equals("CamelCase", StringComparison.OrdinalIgnoreCase))
            {
                target.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            }
            else if (policy.Equals("PascalCase", StringComparison.OrdinalIgnoreCase) ||
                     policy.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                target.PropertyNamingPolicy = null;
            }
            else
            {
                throw new OmniRelayConfigurationException($"Unsupported JSON property naming policy '{configuration.PropertyNamingPolicy}'.");
            }
        }

        if (additionalConverters is not null)
        {
            foreach (var converterType in additionalConverters)
            {
                AddJsonConverter(target, converterType);
            }
        }

        foreach (var converterType in configuration.Converters)
        {
            AddJsonConverter(target, converterType);
        }
    }

    [RequiresDynamicCode("JSON converters are instantiated via reflection.")]
    [RequiresUnreferencedCode("JSON converters are instantiated via reflection.")]
    private static void AddJsonConverter(JsonSerializerOptions target, string converterTypeName)
    {
        if (string.IsNullOrWhiteSpace(converterTypeName))
        {
            return;
        }

        var converterType = ResolveType(converterTypeName, "JSON converter");
        if (!typeof(JsonConverter).IsAssignableFrom(converterType))
        {
            throw new OmniRelayConfigurationException($"Type '{converterTypeName}' is not assignable to JsonConverter.");
        }

        try
        {
            var converter = Activator.CreateInstance(converterType) as JsonConverter
                            ?? throw new OmniRelayConfigurationException($"Unable to instantiate JSON converter '{converterTypeName}'.");
            target.Converters.Add(converter);
        }
        catch (OmniRelayConfigurationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OmniRelayConfigurationException($"Failed to instantiate JSON converter '{converterTypeName}'.", ex);
        }
    }

    private string ResolveContentRootPath()
    {
        var environment = _serviceProvider.GetService<IHostEnvironment>();
        if (environment is not null && !string.IsNullOrWhiteSpace(environment.ContentRootPath))
        {
            return environment.ContentRootPath;
        }

        return AppContext.BaseDirectory;
    }

    private sealed record JsonProfileDescriptor(
        JsonSerializerOptionsConfiguration Options,
        IList<string> Converters,
        string? ContextTypeName)
    {
        public JsonSerializerOptionsConfiguration Options { get; init; } = Options;

        public IList<string> Converters { get; init; } = Converters;

        public string? ContextTypeName { get; init; } = ContextTypeName;
    }

    private void ApplyMiddlewareNative(DispatcherOptions dispatcherOptions)
    {
        var inbound = _options.Middleware.Inbound;
        var outbound = _options.Middleware.Outbound;

        AddMiddlewareWithoutReflection(inbound.Unary, dispatcherOptions.UnaryInboundMiddleware);
        AddMiddlewareWithoutReflection(inbound.Oneway, dispatcherOptions.OnewayInboundMiddleware);
        AddMiddlewareWithoutReflection(inbound.Stream, dispatcherOptions.StreamInboundMiddleware);
        AddMiddlewareWithoutReflection(inbound.ClientStream, dispatcherOptions.ClientStreamInboundMiddleware);
        AddMiddlewareWithoutReflection(inbound.Duplex, dispatcherOptions.DuplexInboundMiddleware);

        AddMiddlewareWithoutReflection(outbound.Unary, dispatcherOptions.UnaryOutboundMiddleware);
        AddMiddlewareWithoutReflection(outbound.Oneway, dispatcherOptions.OnewayOutboundMiddleware);
        AddMiddlewareWithoutReflection(outbound.Stream, dispatcherOptions.StreamOutboundMiddleware);
        AddMiddlewareWithoutReflection(outbound.ClientStream, dispatcherOptions.ClientStreamOutboundMiddleware);
        AddMiddlewareWithoutReflection(outbound.Duplex, dispatcherOptions.DuplexOutboundMiddleware);
    }

    [RequiresDynamicCode("Middleware instances are created via dependency injection and reflection.")]
    [RequiresUnreferencedCode("Middleware instances are created via dependency injection and reflection.")]
    private void ApplyMiddleware(DispatcherOptions dispatcherOptions)
    {
        var inbound = _options.Middleware.Inbound;
        var outbound = _options.Middleware.Outbound;

        AddMiddlewareWithReflection(inbound.Unary, dispatcherOptions.UnaryInboundMiddleware);
        AddMiddlewareWithReflection(inbound.Oneway, dispatcherOptions.OnewayInboundMiddleware);
        AddMiddlewareWithReflection(inbound.Stream, dispatcherOptions.StreamInboundMiddleware);
        AddMiddlewareWithReflection(inbound.ClientStream, dispatcherOptions.ClientStreamInboundMiddleware);
        AddMiddlewareWithReflection(inbound.Duplex, dispatcherOptions.DuplexInboundMiddleware);

        AddMiddlewareWithReflection(outbound.Unary, dispatcherOptions.UnaryOutboundMiddleware);
        AddMiddlewareWithReflection(outbound.Oneway, dispatcherOptions.OnewayOutboundMiddleware);
        AddMiddlewareWithReflection(outbound.Stream, dispatcherOptions.StreamOutboundMiddleware);
        AddMiddlewareWithReflection(outbound.ClientStream, dispatcherOptions.ClientStreamOutboundMiddleware);
        AddMiddlewareWithReflection(outbound.Duplex, dispatcherOptions.DuplexOutboundMiddleware);
    }

    [RequiresDynamicCode("Middleware instances are created via dependency injection and reflection.")]
    [RequiresUnreferencedCode("Middleware instances are created via dependency injection and reflection.")]
    private void AddMiddlewareWithReflection<TMiddleware>(IEnumerable<string> typeNames, IList<TMiddleware> targetList)
    {
        foreach (var typeName in typeNames)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                continue;
            }

            var type = ResolveType(typeName);
            if (!typeof(TMiddleware).IsAssignableFrom(type))
            {
                throw new OmniRelayConfigurationException(
                    $"Configured middleware '{typeName}' does not implement {typeof(TMiddleware).Name}.");
            }

            var instance = (TMiddleware)ActivatorUtilities.CreateInstance(_serviceProvider, type);
            targetList.Add(instance);
        }
    }

    private void AddMiddlewareWithoutReflection<TMiddleware>(IEnumerable<string> typeNames, IList<TMiddleware> targetList)
    {
        foreach (var typeName in typeNames)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                continue;
            }

            if (NativeAotTypeRegistry.TryResolveMiddleware(typeName, out var knownType) &&
                typeof(TMiddleware).IsAssignableFrom(knownType))
            {
                var knownInstance = (TMiddleware)ActivatorUtilities.CreateInstance(_serviceProvider, knownType);
                targetList.Add(knownInstance);
                continue;
            }

            if (_nativeAotStrict)
            {
                throw new OmniRelayConfigurationException(
                    $"Middleware '{typeName}' is not registered for native AOT bootstrap. Add it to the registry or disable strict mode.");
            }

#pragma warning disable IL2026, IL3050
            var resolved = ResolveType(typeName);
#pragma warning restore IL2026, IL3050
            if (!typeof(TMiddleware).IsAssignableFrom(resolved))
            {
                throw new OmniRelayConfigurationException(
                    $"Configured middleware '{typeName}' does not implement {typeof(TMiddleware).Name}.");
            }

#pragma warning disable IL2072
            var instance = (TMiddleware)ActivatorUtilities.CreateInstance(_serviceProvider, resolved);
#pragma warning restore IL2072
            targetList.Add(instance);
        }
    }

    private static ClientCertificateMode ParseClientCertificateMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ClientCertificateMode.NoCertificate;
        }

        if (Enum.TryParse<ClientCertificateMode>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw new OmniRelayConfigurationException(
            $"Unsupported client certificate mode '{value}'. Supported values: NoCertificate, AllowCertificate, RequireCertificate, RequireCertificateAndVerify.");
    }

    private static string ValidateHttpUrl(string value, string context)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new OmniRelayConfigurationException($"The url for {context} cannot be empty.");
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new OmniRelayConfigurationException($"The url '{value}' for {context} is not a valid HTTP/HTTPS address.");
        }

        return uri.ToString();
    }

    private static string ValidateGrpcUrl(string value, string context)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new OmniRelayConfigurationException($"The url/address for {context} cannot be empty.");
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        return Uri.TryCreate($"http://{value}", UriKind.Absolute, out var fallback) ? fallback.ToString() : throw new OmniRelayConfigurationException($"The value '{value}' for {context} is not a valid URI.");
    }

    [RequiresDynamicCode("Type resolution relies on runtime reflection.")]
    [RequiresUnreferencedCode("Calls System.Reflection.Assembly.GetType(String, Boolean, Boolean)")]
    private static Type ResolveType(string typeName)
    {
        var resolved = Type.GetType(typeName, throwOnError: false, ignoreCase: false);
        if (resolved is not null)
        {
            return resolved;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            resolved = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        throw new OmniRelayConfigurationException($"Type '{typeName}' could not be resolved. Ensure the assembly is loaded and the type name is fully qualified.");
    }

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

    private enum OutboundKind
    {
        Unary,
        Oneway,
        Stream,
        ClientStream,
        Duplex
    }
}
