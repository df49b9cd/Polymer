using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using OmniRelay.Identity;

namespace OmniRelay.ControlPlane.Identity;

/// <summary>DI helpers for the in-process certificate authority.</summary>
public static class CertificateAuthorityServiceCollectionExtensions
{
    public static IServiceCollection AddCertificateAuthority(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<CertificateAuthorityOptions>()
            .Configure(options =>
            {
                options.IssuerName = configuration[nameof(CertificateAuthorityOptions.IssuerName)] ?? options.IssuerName;
                options.RootPfxPath = configuration[nameof(CertificateAuthorityOptions.RootPfxPath)] ?? options.RootPfxPath;
                options.RootPfxPassword = configuration[nameof(CertificateAuthorityOptions.RootPfxPassword)] ?? options.RootPfxPassword;
                options.TrustDomain = configuration[nameof(CertificateAuthorityOptions.TrustDomain)] ?? options.TrustDomain;
                options.RequireNodeBinding = TryGetBool(configuration, nameof(CertificateAuthorityOptions.RequireNodeBinding)) ?? options.RequireNodeBinding;
                options.RenewalWindow = TryGetDouble(configuration, nameof(CertificateAuthorityOptions.RenewalWindow)) ?? options.RenewalWindow;
                options.RootReloadInterval = TryGetTimeSpan(configuration, nameof(CertificateAuthorityOptions.RootReloadInterval)) ?? options.RootReloadInterval;
                options.RootLifetime = TryGetTimeSpan(configuration, nameof(CertificateAuthorityOptions.RootLifetime)) ?? options.RootLifetime;
                options.LeafLifetime = TryGetTimeSpan(configuration, nameof(CertificateAuthorityOptions.LeafLifetime)) ?? options.LeafLifetime;
            });

        return services.AddCertificateAuthority();
    }

    public static IServiceCollection AddCertificateAuthority(this IServiceCollection services, Action<CertificateAuthorityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<CertificateAuthorityOptions>, DefaultCertificateAuthorityOptions>());
        }

        services.TryAddSingleton<CertificateAuthorityService>();
        return services;
    }

    private sealed class DefaultCertificateAuthorityOptions : IConfigureOptions<CertificateAuthorityOptions>
    {
        public void Configure(CertificateAuthorityOptions options)
        {
            // defaults already populated by option initializer
        }
    }

    private static bool? TryGetBool(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static double? TryGetDouble(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static TimeSpan? TryGetTimeSpan(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}
