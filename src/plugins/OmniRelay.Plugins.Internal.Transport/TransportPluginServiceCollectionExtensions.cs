using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Core.Transport;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;

namespace OmniRelay.Plugins.Internal.Transport;

/// <summary>Service registration entry point for built-in transport adapters.</summary>
public static class TransportPluginServiceCollectionExtensions
{
    /// <summary>
    /// Registers the built-in HTTP/3 and gRPC inbound transports as <see cref="ITransport"/> adapters. Intended as a first step toward extracting transport wiring into plugins.
    /// </summary>
    public static IServiceCollection AddInternalTransportPlugins(this IServiceCollection services, Action<TransportPluginOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new TransportPluginOptions();
        configure?.Invoke(options);

        RegisterHttpInbound(services, options);
        RegisterGrpcInbound(services, options);

        return services;
    }

    private static void RegisterHttpInbound(IServiceCollection services, TransportPluginOptions options)
    {
        if (options.HttpUrls.Count == 0)
        {
            return;
        }

        var inboundResult = HttpInbound.TryCreate(
            options.HttpUrls,
            configureServices: static services =>
            {
                services.AddTransportSecurityDefaults();
                services.AddHttpTelemetry();
            },
            configureApp: null,
            serverRuntimeOptions: options.HttpRuntime,
            serverTlsOptions: options.HttpTls,
            transportSecurity: null,
            authorizationEvaluator: null);

        if (inboundResult.IsFailure)
        {
            throw new InvalidOperationException(inboundResult.Error?.Message ?? "Failed to create HTTP inbound transport.");
        }

        var inbound = inboundResult.Value;
        services.AddSingleton<ITransport>(_ => new LifecycleTransportAdapter("http", inbound));
    }

    private static void RegisterGrpcInbound(IServiceCollection services, TransportPluginOptions options)
    {
        if (options.GrpcUrls.Count == 0)
        {
            return;
        }

        var inboundResult = GrpcInbound.TryCreate(
            options.GrpcUrls,
            configureServices: static services =>
            {
                services.AddTransportSecurityDefaults();
                services.AddGrpcTelemetry();
            },
            serverRuntimeOptions: options.GrpcRuntime,
            serverTlsOptions: options.GrpcTls,
            telemetryOptions: null,
            transportSecurity: null,
            authorizationEvaluator: null,
            configureApp: null);
    
        if (inboundResult.IsFailure)
        {
            throw new InvalidOperationException(inboundResult.Error?.Message ?? "Failed to create gRPC inbound transport.");
        }

        var inbound = inboundResult.Value;
        services.AddSingleton<ITransport>(_ => new LifecycleTransportAdapter("grpc", inbound));
    }
}
