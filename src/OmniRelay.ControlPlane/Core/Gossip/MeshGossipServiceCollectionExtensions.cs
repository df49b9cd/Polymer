using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmniRelay.Diagnostics;
using OmniRelay.Security.Secrets;

namespace OmniRelay.Core.Gossip;

/// <summary>DI helpers to register the mesh gossip agent.</summary>
public static class MeshGossipServiceCollectionExtensions
{
    public static IServiceCollection AddMeshGossipAgent(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<MeshGossipOptions>()
            .Configure(options => BindMeshGossipOptions(configuration, options));
        return services.AddMeshGossipAgent();
    }

    public static IServiceCollection AddMeshGossipAgent(this IServiceCollection services, Action<MeshGossipOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<MeshGossipOptions>, DefaultMeshGossipOptions>());
        }

        services.AddSingleton<IMeshGossipAgent>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<MeshGossipOptions>>().Value;
            if (!options.Enabled)
            {
                return NullMeshGossipAgent.Instance;
            }

            var logger = sp.GetRequiredService<ILogger<MeshGossipHost>>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var tracker = sp.GetService<PeerLeaseHealthTracker>();
            var secretProvider = sp.GetService<ISecretProvider>();
            return new MeshGossipHost(options, metadata: null, logger, loggerFactory, timeProvider, tracker, secretProvider: secretProvider);
        });
        services.AddSingleton<IMeshMembershipSnapshotProvider>(sp => sp.GetRequiredService<IMeshGossipAgent>());

        services.AddSingleton<IHostedService>(sp =>
        {
            var agent = sp.GetRequiredService<IMeshGossipAgent>();
            var logger = sp.GetRequiredService<ILogger<MeshGossipHostedService>>();
            return new MeshGossipHostedService(agent, logger);
        });

        return services;
    }

    private sealed class DefaultMeshGossipOptions : IConfigureOptions<MeshGossipOptions>
    {
        public void Configure(MeshGossipOptions options)
        {
            // No-op placeholder so the options system can resolve a value even if not configured.
        }
    }

    private static void BindMeshGossipOptions(IConfiguration configuration, MeshGossipOptions options)
    {
        options.Enabled = ReadBool(configuration, nameof(MeshGossipOptions.Enabled)) ?? options.Enabled;
        options.NodeId = ReadString(configuration, nameof(MeshGossipOptions.NodeId)) ?? options.NodeId;
        options.Role = ReadString(configuration, nameof(MeshGossipOptions.Role)) ?? options.Role;
        options.ClusterId = ReadString(configuration, nameof(MeshGossipOptions.ClusterId)) ?? options.ClusterId;
        options.Region = ReadString(configuration, nameof(MeshGossipOptions.Region)) ?? options.Region;
        options.MeshVersion = ReadString(configuration, nameof(MeshGossipOptions.MeshVersion)) ?? options.MeshVersion;
        options.Http3Support = ReadBool(configuration, nameof(MeshGossipOptions.Http3Support)) ?? options.Http3Support;
        options.BindAddress = ReadString(configuration, nameof(MeshGossipOptions.BindAddress)) ?? options.BindAddress;
        options.Port = ReadInt(configuration, nameof(MeshGossipOptions.Port)) ?? options.Port;
        options.AdvertiseHost = ReadString(configuration, nameof(MeshGossipOptions.AdvertiseHost)) ?? options.AdvertiseHost;
        options.AdvertisePort = ReadInt(configuration, nameof(MeshGossipOptions.AdvertisePort)) ?? options.AdvertisePort;
        options.Interval = ReadTimeSpan(configuration, nameof(MeshGossipOptions.Interval)) ?? options.Interval;
        options.Fanout = ReadInt(configuration, nameof(MeshGossipOptions.Fanout)) ?? options.Fanout;
        options.FanoutCeiling = ReadInt(configuration, nameof(MeshGossipOptions.FanoutCeiling)) ?? options.FanoutCeiling;
        options.FanoutFloor = ReadInt(configuration, nameof(MeshGossipOptions.FanoutFloor)) ?? options.FanoutFloor;
        options.FanoutCoefficient = ReadDouble(configuration, nameof(MeshGossipOptions.FanoutCoefficient)) ?? options.FanoutCoefficient;
        options.AdaptiveFanout = ReadBool(configuration, nameof(MeshGossipOptions.AdaptiveFanout)) ?? options.AdaptiveFanout;
        options.MaxOutboundPerRound = ReadInt(configuration, nameof(MeshGossipOptions.MaxOutboundPerRound)) ?? options.MaxOutboundPerRound;
        options.ActiveViewSize = ReadInt(configuration, nameof(MeshGossipOptions.ActiveViewSize)) ?? options.ActiveViewSize;
        options.PassiveViewSize = ReadInt(configuration, nameof(MeshGossipOptions.PassiveViewSize)) ?? options.PassiveViewSize;
        options.ShuffleInterval = ReadTimeSpan(configuration, nameof(MeshGossipOptions.ShuffleInterval)) ?? options.ShuffleInterval;
        options.ShuffleSampleSize = ReadInt(configuration, nameof(MeshGossipOptions.ShuffleSampleSize)) ?? options.ShuffleSampleSize;
        options.SuspicionInterval = ReadTimeSpan(configuration, nameof(MeshGossipOptions.SuspicionInterval)) ?? options.SuspicionInterval;
        options.PingTimeout = ReadTimeSpan(configuration, nameof(MeshGossipOptions.PingTimeout)) ?? options.PingTimeout;
        options.RetransmitLimit = ReadInt(configuration, nameof(MeshGossipOptions.RetransmitLimit)) ?? options.RetransmitLimit;
        options.MetadataRefreshPeriod = ReadTimeSpan(configuration, nameof(MeshGossipOptions.MetadataRefreshPeriod)) ?? options.MetadataRefreshPeriod;
        options.CertificateReloadInterval = ReadTimeSpan(configuration, nameof(MeshGossipOptions.CertificateReloadInterval)) ?? options.CertificateReloadInterval;

        options.Labels.Clear();
        foreach (var entry in configuration.GetSection(nameof(MeshGossipOptions.Labels)).GetChildren())
        {
            options.Labels[entry.Key] = entry.Value ?? string.Empty;
        }

        options.SeedPeers.Clear();
        foreach (var peer in configuration.GetSection(nameof(MeshGossipOptions.SeedPeers)).GetChildren())
        {
            var value = peer.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                options.SeedPeers.Add(value.Trim());
            }
        }

        var tlsSection = configuration.GetSection(nameof(MeshGossipOptions.Tls));
        BindMeshGossipTlsOptions(tlsSection, options.Tls);
    }

    private static void BindMeshGossipTlsOptions(IConfiguration section, MeshGossipTlsOptions tls)
    {
        tls.CertificatePath = ReadString(section, nameof(MeshGossipTlsOptions.CertificatePath)) ?? tls.CertificatePath;
        tls.CertificateData = ReadString(section, nameof(MeshGossipTlsOptions.CertificateData)) ?? tls.CertificateData;
        tls.CertificateDataSecret = ReadString(section, nameof(MeshGossipTlsOptions.CertificateDataSecret)) ?? tls.CertificateDataSecret;
        tls.CertificatePassword = ReadString(section, nameof(MeshGossipTlsOptions.CertificatePassword)) ?? tls.CertificatePassword;
        tls.CertificatePasswordSecret = ReadString(section, nameof(MeshGossipTlsOptions.CertificatePasswordSecret)) ?? tls.CertificatePasswordSecret;
        tls.AllowUntrustedCertificates = ReadBool(section, nameof(MeshGossipTlsOptions.AllowUntrustedCertificates)) ?? tls.AllowUntrustedCertificates;
        tls.CheckCertificateRevocation = ReadBool(section, nameof(MeshGossipTlsOptions.CheckCertificateRevocation)) ?? tls.CheckCertificateRevocation;
        tls.ReloadIntervalOverride = ReadTimeSpan(section, nameof(MeshGossipTlsOptions.ReloadIntervalOverride)) ?? tls.ReloadIntervalOverride;

        tls.AllowedThumbprints.Clear();
        foreach (var thumbprint in section.GetSection(nameof(MeshGossipTlsOptions.AllowedThumbprints)).GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(thumbprint.Value))
            {
                tls.AllowedThumbprints.Add(thumbprint.Value.Trim());
            }
        }
    }

    private static string? ReadString(IConfiguration configuration, string key) => configuration[key];

    private static bool? ReadBool(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static int? ReadInt(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static double? ReadDouble(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static TimeSpan? ReadTimeSpan(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}

internal sealed class MeshGossipHostedService(IMeshGossipAgent agent, ILogger<MeshGossipHostedService> logger) : IHostedService
{
    private readonly IMeshGossipAgent _agent = agent ?? throw new ArgumentNullException(nameof(agent));
    private readonly ILogger<MeshGossipHostedService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_agent.IsEnabled)
        {
            MeshGossipHostedServiceLog.Starting(_logger, _agent.LocalMetadata.NodeId);
            await _agent.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_agent.IsEnabled)
        {
            await _agent.StopAsync(cancellationToken).ConfigureAwait(false);
            MeshGossipHostedServiceLog.Stopped(_logger, _agent.LocalMetadata.NodeId);
        }
    }
}

internal static partial class MeshGossipHostedServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting mesh gossip agent for node {NodeId}")]
    public static partial void Starting(ILogger logger, string nodeId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Stopped mesh gossip agent for node {NodeId}")]
    public static partial void Stopped(ILogger logger, string nodeId);
}
