using System.Collections.Immutable;
using Hugo;
using OmniRelay.Core.Transport;

namespace OmniRelay.Core.Middleware;

/// <summary>
/// Enriches inbound requests with principal metadata sourced from TLS client certificates or auth headers.
/// </summary>
public sealed class PrincipalBindingMiddleware :
    IUnaryInboundMiddleware,
    IOnewayInboundMiddleware,
    IStreamInboundMiddleware,
    IClientStreamInboundMiddleware,
    IDuplexInboundMiddleware
{
    private readonly PrincipalBindingOptions _options;

    public PrincipalBindingMiddleware(PrincipalBindingOptions? options = null)
    {
        _options = options ?? new PrincipalBindingOptions();
        if (_options.PrincipalHeaderNames.Length == 0 && _options.AuthorizationHeaderNames.Length == 0)
        {
            throw new ArgumentException("Configure at least one principal or authorization header.", nameof(options));
        }
    }

    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        var enriched = EnrichRequest(request);
        return nextHandler(enriched, cancellationToken);
    }

    public ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        var enriched = EnrichRequest(request);
        return nextHandler(enriched, cancellationToken);
    }

    public ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamInboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        options = EnsureNotNull(options, nameof(options));

        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        var enriched = EnrichRequest(request);
        return nextHandler(enriched, options, cancellationToken);
    }

    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        ClientStreamRequestContext context,
        CancellationToken cancellationToken,
        ClientStreamInboundHandler nextHandler)
    {
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        var enriched = EnrichContext(context);
        return nextHandler(enriched, cancellationToken);
    }

    public ValueTask<Result<IDuplexStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        DuplexInboundHandler nextHandler)
    {
        request = EnsureNotNull(request, nameof(request));
        nextHandler = EnsureNotNull(nextHandler, nameof(nextHandler));

        var enriched = EnrichRequest(request);
        return nextHandler(enriched, cancellationToken);
    }

    private IRequest<ReadOnlyMemory<byte>> EnrichRequest(IRequest<ReadOnlyMemory<byte>> request)
    {
        var meta = BindPrincipal(request.Meta);
        return ReferenceEquals(meta, request.Meta)
            ? request
            : new Request<ReadOnlyMemory<byte>>(meta, request.Body);
    }

    private ClientStreamRequestContext EnrichContext(ClientStreamRequestContext context)
    {
        var meta = BindPrincipal(context.Meta);
        return ReferenceEquals(meta, context.Meta)
            ? context
            : new ClientStreamRequestContext(meta, context.Requests);
    }

    private RequestMeta BindPrincipal(RequestMeta meta)
    {
        var principal = ResolvePrincipal(meta);
        if (string.IsNullOrWhiteSpace(principal))
        {
            return meta;
        }

        var updated = meta.WithHeader(_options.PrincipalMetadataKey, principal!);
        if (_options.PromoteToCaller && string.IsNullOrWhiteSpace(updated.Caller))
        {
            updated = updated with { Caller = principal };
        }

        if (_options.IncludeThumbprint &&
            !string.IsNullOrWhiteSpace(_options.ThumbprintHeaderName) &&
            meta.Headers.TryGetValue(_options.ThumbprintHeaderName, out var thumbprint) &&
            !string.IsNullOrWhiteSpace(thumbprint))
        {
            updated = updated.WithHeader(_options.PrincipalThumbprintMetadataKey, thumbprint!);
        }

        return updated;
    }

    private string? ResolvePrincipal(RequestMeta meta)
    {
        foreach (var header in _options.PrincipalHeaderNames)
        {
            if (meta.Headers.TryGetValue(header, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value!.Trim();
            }
        }

        foreach (var header in _options.AuthorizationHeaderNames)
        {
            if (!meta.Headers.TryGetValue(header, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value!.Trim();
            if (_options.AcceptBearerTokens && trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[7..].Trim();
            }

            if (_options.AcceptMutualTlsSubjects && trimmed.StartsWith("mTLS ", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[5..].Trim();
            }
        }

        return null;
    }

    private static T EnsureNotNull<T>(T? value, string paramName) where T : class
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        return value;
    }
}

/// <summary>Options controlling how principals are inferred from inbound metadata.</summary>
public sealed class PrincipalBindingOptions
{
    public const string DefaultPrincipalMetadataKey = "rpc.principal";

    private static readonly string[] DefaultPrincipalHeaders = [DefaultPrincipalMetadataKey, "x-client-principal", "x-mtls-subject", "x-peer-id"];
    private static readonly string[] DefaultAuthorizationHeaders = ["authorization"];

    /// <summary>Header names evaluated (in order) for a client principal.</summary>
    public ImmutableArray<string> PrincipalHeaderNames { get; init; } = [.. DefaultPrincipalHeaders];

    /// <summary>Authorization headers evaluated when no explicit principal header is present.</summary>
    public ImmutableArray<string> AuthorizationHeaderNames { get; init; } = [.. DefaultAuthorizationHeaders];

    /// <summary>When true, the resolved principal replaces <see cref="RequestMeta.Caller"/> when it is empty.</summary>
    public bool PromoteToCaller { get; init; } = true;

    /// <summary>Metadata key storing the normalized principal. Defaults to 'rpc.principal'.</summary>
    public string PrincipalMetadataKey { get; init; } = DefaultPrincipalMetadataKey;

    /// <summary>Header that carries the TLS client thumbprint (if termination populates it).</summary>
    public string ThumbprintHeaderName { get; init; } = "x-mtls-thumbprint";

    /// <summary>Metadata key storing the TLS thumbprint when <see cref="IncludeThumbprint"/> is true.</summary>
    public string PrincipalThumbprintMetadataKey { get; init; } = "rpc.principal_thumbprint";

    /// <summary>Captures the certificate thumbprint into metadata when true.</summary>
    public bool IncludeThumbprint { get; init; } = true;

    /// <summary>When true, bearer tokens found in authorization headers will be treated as principals.</summary>
    public bool AcceptBearerTokens { get; init; } = true;

    /// <summary>When true, authorization headers prefixed with 'mTLS ' are parsed as certificate subjects.</summary>
    public bool AcceptMutualTlsSubjects { get; init; } = true;
}

