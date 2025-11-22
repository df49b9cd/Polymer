using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace OmniRelay.Diagnostics;

/// <summary>Extension helpers for registering OmniRelay documentation endpoints.</summary>
public static class OmniRelayDocumentationExtensions
{
    public static IServiceCollection AddOmniRelayDocumentation(
        this IServiceCollection services,
        Action<OmniRelayDocumentationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<OmniRelayDocumentationOptions>()
            .Configure(options => configure?.Invoke(options));

        services.AddOpenApi();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOpenApiDocumentTransformer, OmniRelayMetadataDocumentTransformer>());
        services.AddGrpcReflection();
        return services;
    }

    public static IEndpointRouteBuilder MapOmniRelayDocumentation(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = builder.ServiceProvider.GetRequiredService<IOptions<OmniRelayDocumentationOptions>>().Value;

        if (options.EnableOpenApi)
        {
            var openApi = builder.MapOpenApi(options.RoutePattern);
            if (!string.IsNullOrWhiteSpace(options.AuthorizationPolicy))
            {
                openApi.RequireAuthorization(options.AuthorizationPolicy);
            }
        }

        if (options.EnableGrpcReflection)
        {
            var grpc = builder.MapGrpcReflectionService();
            if (!string.IsNullOrWhiteSpace(options.AuthorizationPolicy))
            {
                grpc.RequireAuthorization(options.AuthorizationPolicy);
            }
        }

        return builder;
    }
}
