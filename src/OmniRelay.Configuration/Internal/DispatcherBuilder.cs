using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Grpc.Core.Interceptors;
using Json.Schema;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniRelay.Configuration.Models;
using OmniRelay.Core;
using OmniRelay.Core.Diagnostics;
using OmniRelay.Core.Peers;
using OmniRelay.Dispatcher;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace OmniRelay.Configuration.Internal;

/// <summary>
/// Builds a configured <see cref="Dispatcher.Dispatcher"/> from bound <see cref="Models.OmniRelayConfigurationOptions"/> and the service provider.
/// </summary>
internal sealed class DispatcherBuilder
{
    private readonly OmniRelayConfigurationOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly Dictionary<string, HttpOutbound> _httpOutboundCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GrpcOutbound> _grpcOutboundCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, ICustomInboundSpec> _customInboundSpecs;
    private readonly IReadOnlyDictionary<string, ICustomOutboundSpec> _customOutboundSpecs;
    private readonly IReadOnlyDictionary<string, ICustomPeerChooserSpec> _customPeerSpecs;

    public DispatcherBuilder(OmniRelayConfigurationOptions options, IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

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

        return new Dispatcher.Dispatcher(dispatcherOptions);
    }

    private void ConfigureInbounds(DispatcherOptions dispatcherOptions)
    {
        ConfigureHttpInbounds(dispatcherOptions);
        ConfigureGrpcInbounds(dispatcherOptions);
        ConfigureCustomInbounds(dispatcherOptions);
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
            dispatcherOptions.AddLifecycle(name, new HttpInbound(urls, configureServices: configureServices, configureApp: configureApp, serverRuntimeOptions: httpRuntimeOptions, serverTlsOptions: httpTlsOptions));
            index++;
        }
    }

    private void ConfigureGrpcInbounds(DispatcherOptions dispatcherOptions)
    {
        var index = 0;
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

            dispatcherOptions.AddLifecycle(
                name,
                new GrpcInbound(
                    urls,
                    serverRuntimeOptions: runtimeOptions,
                    serverTlsOptions: tlsOptions,
                    telemetryOptions: telemetryOptions));

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

    private void ConfigureOutbounds(DispatcherOptions dispatcherOptions)
    {
        foreach (var (service, config) in _options.Outbounds)
        {
            if (string.IsNullOrWhiteSpace(service))
            {
                continue;
            }

            var serviceSection = _configuration.GetSection($"outbounds:{service}");

            RegisterOutboundSet(dispatcherOptions, service, config.Unary, serviceSection, OutboundKind.Unary);
            RegisterOutboundSet(dispatcherOptions, service, config.Oneway, serviceSection, OutboundKind.Oneway);
            RegisterOutboundSet(dispatcherOptions, service, config.Stream, serviceSection, OutboundKind.Stream);
            RegisterOutboundSet(dispatcherOptions, service, config.ClientStream, serviceSection, OutboundKind.ClientStream);
            RegisterOutboundSet(dispatcherOptions, service, config.Duplex, serviceSection, OutboundKind.Duplex);
        }
    }

    private void RegisterOutboundSet(
        DispatcherOptions dispatcherOptions,
        string service,
        RpcOutboundConfiguration? configuration,
        IConfigurationSection? serviceSection,
        OutboundKind kind)
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

        ConfigureCustomOutbounds(dispatcherOptions, service, kind, kindSection);
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

        var cacheKey = FormattableString.Invariant($"{service}|{configuration.Key ?? OutboundCollection.DefaultKey}|{uri}|{configuration.ClientName ?? string.Empty}|{runtimeKey}");

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

        var cacheKey = FormattableString.Invariant($"{service}|{configuration.Key ?? OutboundCollection.DefaultKey}|{remoteService}|{string.Join(",", uris.Select(u => u.ToString()))}|{configuration.PeerChooser ?? "round-robin"}");

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

    private static GrpcClientTlsOptions? BuildGrpcClientTlsOptions(GrpcClientTlsConfiguration configuration)
    {
        if (configuration is null)
        {
            return null;
        }

        var hasValues =
            !string.IsNullOrWhiteSpace(configuration.CertificatePath) ||
            !string.IsNullOrWhiteSpace(configuration.CertificatePassword) ||
            configuration.AllowUntrustedCertificates.HasValue ||
            !string.IsNullOrWhiteSpace(configuration.TargetNameOverride);

        if (!hasValues)
        {
            return null;
        }

        var certificates = new X509Certificate2Collection();
        if (!string.IsNullOrWhiteSpace(configuration.CertificatePath))
        {
            var path = ResolvePath(configuration.CertificatePath!);
            if (!File.Exists(path))
            {
                throw new OmniRelayConfigurationException($"Client TLS certificate file '{path}' could not be found.");
            }

            X509Certificate2 cert;
#pragma warning disable SYSLIB0057
            cert = string.IsNullOrEmpty(configuration.CertificatePassword)
                ? new X509Certificate2(path)
                : new X509Certificate2(path, configuration.CertificatePassword);
#pragma warning restore SYSLIB0057
            certificates.Add(cert);
        }

        if (!string.IsNullOrWhiteSpace(configuration.TargetNameOverride))
        {
            throw new OmniRelayConfigurationException("gRPC client target name override is not yet supported in configuration.");
        }

        RemoteCertificateValidationCallback? validationCallback = null;
        if (configuration.AllowUntrustedCertificates == true)
        {
            validationCallback = (_, _, _, _) => true;
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

    private IReadOnlyList<Interceptor> ResolveClientInterceptors(IEnumerable<string> typeNames)
    {
        var resolved = new List<Interceptor>();
        foreach (var typeName in typeNames)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                continue;
            }

            var type = ResolveType(typeName);
            if (!typeof(Interceptor).IsAssignableFrom(type))
            {
                throw new OmniRelayConfigurationException(
                    $"Configured gRPC client interceptor '{typeName}' does not derive from {nameof(Interceptor)}.");
            }

            var instance = (Interceptor)ActivatorUtilities.CreateInstance(_serviceProvider, type);
            resolved.Add(instance);
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

        if (TryGetDiagnosticsControlPlaneOptions(out var controlPlaneOptions))
        {
            actions.Add(app => ConfigureDiagnosticsControlPlane(app, controlPlaneOptions));
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
        // The HTTP inbound hosts its own minimal WebApplication instance with its own DI container.
        // We may need to register a MeterProvider (for Prometheus scraping) and diagnostics runtime services
        // (for control-plane endpoints) in that container.

        var addMetrics = ShouldExposePrometheusMetrics();
        var addControlPlane = TryGetDiagnosticsControlPlaneOptions(out var controlPlaneOptions);

        if (!addMetrics && !addControlPlane)
        {
            return null;
        }

        var scrapePath = NormalizeScrapeEndpointPathForInbound(_options.Diagnostics?.OpenTelemetry?.Prometheus?.ScrapeEndpointPath);
        var rootRuntime = _serviceProvider.GetService<IDiagnosticsRuntime>();
        var peerHealthProviders = controlPlaneOptions.EnableLeaseHealthDiagnostics
            ? _serviceProvider.GetServices<IPeerHealthSnapshotProvider>()
                .Where(static provider => provider is not null)
                .ToArray()
            : [];

        return services =>
        {
            if (addMetrics)
            {
                var otel = services.AddOpenTelemetry();
                // Align resource identity with the configured service name for consistent labels.
                var serviceName = string.IsNullOrWhiteSpace(_options.Service) ? "OmniRelay" : _options.Service!;
                otel.ConfigureResource(resource => resource.AddService(serviceName: serviceName));
                otel.WithMetrics(builder =>
                {
                    builder.AddMeter("OmniRelay.Core.Peers", "OmniRelay.Transport.Grpc", "OmniRelay.Transport.Http", "OmniRelay.Rpc", "Hugo.Go");
                    builder.AddPrometheusExporter(options =>
                    {
                        options.ScrapeEndpointPath = scrapePath;
                    });
                });
            }

            if (addControlPlane)
            {
                // Bridge diagnostics runtime into the inbound DI container so minimal APIs can resolve it from services.
                if (rootRuntime is not null)
                {
                    services.AddSingleton(rootRuntime);
                }
                else
                {
                    services.AddSingleton<IDiagnosticsRuntime, DiagnosticsRuntimeState>();
                }

                // Also bridge the root logger factory so inbound logging honors global policies.
                var rootLoggerFactory = _serviceProvider.GetService<ILoggerFactory>();
                if (rootLoggerFactory is not null)
                {
                    services.AddSingleton(rootLoggerFactory);
                }

                if (controlPlaneOptions.EnableLeaseHealthDiagnostics && peerHealthProviders.Length > 0)
                {
                    services.AddSingleton<IEnumerable<IPeerHealthSnapshotProvider>>(peerHealthProviders);
                }
            }
        };
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

    private static HttpServerTlsOptions? BuildHttpServerTlsOptions(HttpServerTlsConfiguration configuration)
    {
        if (configuration is null || string.IsNullOrWhiteSpace(configuration.CertificatePath))
        {
            return null;
        }

        var path = ResolvePath(configuration.CertificatePath!);
        if (!File.Exists(path))
        {
            throw new OmniRelayConfigurationException($"HTTP server certificate '{path}' could not be found.");
        }

        X509Certificate2 certificate;
#pragma warning disable SYSLIB0057
        certificate = string.IsNullOrEmpty(configuration.CertificatePassword)
            ? new X509Certificate2(path)
            : new X509Certificate2(path, configuration.CertificatePassword);
#pragma warning restore SYSLIB0057

        var mode = ParseClientCertificateMode(configuration.ClientCertificateMode);

        return new HttpServerTlsOptions
        {
            Certificate = certificate,
            ClientCertificateMode = mode,
            CheckCertificateRevocation = configuration.CheckCertificateRevocation
        };
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

    private bool TryGetDiagnosticsControlPlaneOptions(out DiagnosticsControlPlaneOptions options)
    {
        var runtime = _options.Diagnostics?.Runtime;
        if (runtime is null)
        {
            options = default;
            return false;
        }

        var enableLogging = runtime.EnableLoggingLevelToggle ?? false;
        var enableSampling = runtime.EnableTraceSamplingToggle ?? false;
        var enableControlPlane = runtime.EnableControlPlane ?? (enableLogging || enableSampling);

        if (!enableControlPlane)
        {
            options = default;
            return false;
        }

        var hasPeerHealthProviders = _serviceProvider.GetServices<IPeerHealthSnapshotProvider>().Any();

        options = new DiagnosticsControlPlaneOptions(enableLogging, enableSampling, hasPeerHealthProviders);
        return enableLogging || enableSampling || hasPeerHealthProviders;
    }

    [RequiresDynamicCode("Calls Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapGet(String, Delegate)")]
    [RequiresUnreferencedCode("Calls Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapGet(String, Delegate)")]
    private static void ConfigureDiagnosticsControlPlane(WebApplication app, DiagnosticsControlPlaneOptions options)
    {
        if (options.EnableLoggingToggle)
        {
            app.MapGet("/omnirelay/control/logging", (IDiagnosticsRuntime runtime) =>
            {
                var level = runtime.MinimumLogLevel?.ToString();
                return Results.Json(new { minimumLevel = level });
            });

            app.MapPost("/omnirelay/control/logging", (DiagnosticsLogLevelRequest request, IDiagnosticsRuntime runtime) =>
            {
                if (request is null)
                {
                    return Results.BadRequest(new { error = "Request body required." });
                }

                if (string.IsNullOrWhiteSpace(request.Level))
                {
                    runtime.SetMinimumLogLevel(null);
                    return Results.NoContent();
                }

                if (!Enum.TryParse<LogLevel>(request.Level, ignoreCase: true, out var parsed))
                {
                    return Results.BadRequest(new { error = $"Invalid log level '{request.Level}'." });
                }

                runtime.SetMinimumLogLevel(parsed);
                return Results.NoContent();
            });
        }

        if (options.EnableSamplingToggle)
        {
            app.MapGet("/omnirelay/control/tracing", (IDiagnosticsRuntime runtime) =>
            {
                return Results.Json(new { samplingProbability = runtime.TraceSamplingProbability });
            });

            app.MapPost("/omnirelay/control/tracing", (DiagnosticsSamplingRequest request, IDiagnosticsRuntime runtime) =>
            {
                if (request is null)
                {
                    return Results.BadRequest(new { error = "Request body required." });
                }

                try
                {
                    runtime.SetTraceSamplingProbability(request.Probability);
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }

                return Results.NoContent();
            });
        }

        if (options.EnableLeaseHealthDiagnostics)
        {
            app.MapGet("/omnirelay/control/lease-health", (IEnumerable<IPeerHealthSnapshotProvider> providers) =>
            {
                var builder = ImmutableArray.CreateBuilder<PeerLeaseHealthSnapshot>();
                foreach (var provider in providers)
                {
                    if (provider is null)
                    {
                        continue;
                    }

                    var snapshot = provider.Snapshot();
                    if (!snapshot.IsDefaultOrEmpty)
                    {
                        builder.AddRange(snapshot);
                    }
                }

                var diagnostics = PeerLeaseHealthDiagnostics.FromSnapshots(builder.ToImmutable());
                return Results.Json(diagnostics);
            });
        }
    }

    private readonly record struct DiagnosticsControlPlaneOptions(bool EnableLoggingToggle, bool EnableSamplingToggle, bool EnableLeaseHealthDiagnostics)
    {
        public bool EnableLoggingToggle { get; init; } = EnableLoggingToggle;

        public bool EnableSamplingToggle { get; init; } = EnableSamplingToggle;

        public bool EnableLeaseHealthDiagnostics { get; init; } = EnableLeaseHealthDiagnostics;
    }

    private sealed record DiagnosticsLogLevelRequest(string? Level)
    {
        public string? Level { get; init; } = Level;
    }

    private sealed record DiagnosticsSamplingRequest(double? Probability)
    {
        public double? Probability { get; init; } = Probability;
    }

    private static GrpcServerRuntimeOptions? BuildGrpcServerRuntimeOptions(GrpcServerRuntimeConfiguration configuration)
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

    private static IReadOnlyList<Type> ResolveServerInterceptorTypes(IEnumerable<string> typeNames)
    {
        var resolved = new List<Type>();
        foreach (var typeName in typeNames)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                continue;
            }

            var type = ResolveType(typeName);
            if (!typeof(Interceptor).IsAssignableFrom(type))
            {
                throw new OmniRelayConfigurationException(
                    $"Configured gRPC server interceptor '{typeName}' does not derive from {nameof(Interceptor)}.");
            }

            resolved.Add(type);
        }

        return resolved;
    }

    private static GrpcServerTlsOptions? BuildGrpcServerTlsOptions(GrpcServerTlsConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.CertificatePath))
        {
            return null;
        }

        var path = ResolvePath(configuration.CertificatePath!);
        if (!File.Exists(path))
        {
            throw new OmniRelayConfigurationException($"gRPC server certificate '{path}' could not be found.");
        }

#pragma warning disable SYSLIB0057
        var certificate = string.IsNullOrEmpty(configuration.CertificatePassword)
            ? new X509Certificate2(path)
            : new X509Certificate2(path, configuration.CertificatePassword);
#pragma warning restore SYSLIB0057

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

    private void ConfigureEncodings(DispatcherOptions dispatcherOptions) => ConfigureJsonEncodings(dispatcherOptions);

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

        var materials = BuildJsonCodecMaterials(profiles, registration, requestType, responseType);

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

    private static (JsonSerializerOptions Options, JsonSerializerContext? Context) BuildJsonCodecMaterials(
        IDictionary<string, JsonProfileDescriptor> profiles,
        JsonCodecRegistrationConfiguration registration,
        Type requestType,
        Type responseType)
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

    private void ApplyMiddleware(DispatcherOptions dispatcherOptions)
    {
        var inbound = _options.Middleware.Inbound;
        var outbound = _options.Middleware.Outbound;

        AddMiddleware(inbound.Unary, dispatcherOptions.UnaryInboundMiddleware);
        AddMiddleware(inbound.Oneway, dispatcherOptions.OnewayInboundMiddleware);
        AddMiddleware(inbound.Stream, dispatcherOptions.StreamInboundMiddleware);
        AddMiddleware(inbound.ClientStream, dispatcherOptions.ClientStreamInboundMiddleware);
        AddMiddleware(inbound.Duplex, dispatcherOptions.DuplexInboundMiddleware);

        AddMiddleware(outbound.Unary, dispatcherOptions.UnaryOutboundMiddleware);
        AddMiddleware(outbound.Oneway, dispatcherOptions.OnewayOutboundMiddleware);
        AddMiddleware(outbound.Stream, dispatcherOptions.StreamOutboundMiddleware);
        AddMiddleware(outbound.ClientStream, dispatcherOptions.ClientStreamOutboundMiddleware);
        AddMiddleware(outbound.Duplex, dispatcherOptions.DuplexOutboundMiddleware);
    }

    private void AddMiddleware<TMiddleware>(IEnumerable<string> typeNames, IList<TMiddleware> targetList)
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
