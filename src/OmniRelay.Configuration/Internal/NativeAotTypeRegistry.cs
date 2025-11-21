using System.Collections.Immutable;
using System.Text.Json.Serialization;
using OmniRelay.Core.Middleware;
using OmniRelay.Security.Authorization;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Security;

namespace OmniRelay.Configuration.Internal;

/// <summary>
/// Compile-time registry of components that are safe to activate in Native AOT scenarios without reflection.
/// </summary>
internal static class NativeAotTypeRegistry
{
    private static readonly ImmutableDictionary<string, Type> MiddlewareTypes = BuildIndex(
        (typeof(RpcTracingMiddleware), new[] { "RpcTracingMiddleware", "tracing" }),
        (typeof(RpcLoggingMiddleware), new[] { "RpcLoggingMiddleware", "logging" }),
        (typeof(RpcMetricsMiddleware), new[] { "RpcMetricsMiddleware", "metrics" }),
        (typeof(DeadlineMiddleware), new[] { "DeadlineMiddleware", "deadline" }),
        (typeof(RetryMiddleware), new[] { "RetryMiddleware", "retry" }),
        (typeof(RateLimitingMiddleware), new[] { "RateLimitingMiddleware", "ratelimit", "rate-limiting" }),
        (typeof(PrincipalBindingMiddleware), new[] { "PrincipalBindingMiddleware", "principal" }),
        (typeof(PanicRecoveryMiddleware), new[] { "PanicRecoveryMiddleware", "panic" }));

    private static readonly ImmutableDictionary<string, Type> GrpcClientInterceptorTypes = BuildIndex(
        (typeof(GrpcClientLoggingInterceptor), new[] { "GrpcClientLoggingInterceptor", "grpc-client-logging" }));

    private static readonly ImmutableDictionary<string, Type> GrpcServerInterceptorTypes = BuildIndex(
        (typeof(GrpcExceptionAdapterInterceptor), new[] { "GrpcExceptionAdapterInterceptor", "grpc-exception-adapter" }),
        (typeof(GrpcServerLoggingInterceptor), new[] { "GrpcServerLoggingInterceptor", "grpc-server-logging" }),
        (typeof(TransportSecurityGrpcInterceptor), new[] { "TransportSecurityGrpcInterceptor", "transport-security" }),
        (typeof(MeshAuthorizationGrpcInterceptor), new[] { "MeshAuthorizationGrpcInterceptor", "mesh-authorization" }));

    private static readonly ImmutableDictionary<string, Type> JsonConverterTypes = BuildIndex(
        (typeof(JsonStringEnumConverter), new[] { nameof(JsonStringEnumConverter), "json-string-enum" }));

    internal static bool TryResolveMiddleware(string name, out Type type) =>
        MiddlewareTypes.TryGetValue(Normalize(name), out type!);

    internal static bool TryResolveGrpcClientInterceptor(string name, out Type type) =>
        GrpcClientInterceptorTypes.TryGetValue(Normalize(name), out type!);

    internal static bool TryResolveGrpcServerInterceptor(string name, out Type type) =>
        GrpcServerInterceptorTypes.TryGetValue(Normalize(name), out type!);

    internal static bool TryResolveJsonConverter(string name, out Type type) =>
        JsonConverterTypes.TryGetValue(Normalize(name), out type!);

    private static ImmutableDictionary<string, Type> BuildIndex(params (Type Type, string[] Keys)[] entries)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, keys) in entries)
        {
            foreach (var key in keys)
            {
                Add(builder, type, key);
            }

            Add(builder, type, type.FullName);
            Add(builder, type, type.AssemblyQualifiedName);
        }

        return builder.ToImmutable();

        static void Add(IDictionary<string, Type> dictionary, Type type, string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            dictionary[key] = type;
        }
    }

    private static string Normalize(string name) => name.Trim();
}
