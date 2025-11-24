using System.Collections.Immutable;
using Hugo;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using static Hugo.Go;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Base record describing a registered procedure: name, encoding, kind, and aliases.
/// </summary>
public abstract record ProcedureSpec
{
    protected ProcedureSpec(
        string service,
        string name,
        ProcedureKind kind,
        string? encoding = null,
        IReadOnlyList<string>? aliases = null)
    {
        Service = service ?? throw new ArgumentNullException(nameof(service));
        Name = name;
        Kind = kind;
        Encoding = encoding;
        Aliases = aliases is null
            ? []
            : [.. aliases];
    }

    /// <summary>Gets the service name.</summary>
    public string Service { get; }

    /// <summary>Gets the procedure name.</summary>
    public string Name { get; }

    /// <summary>Gets the RPC shape.</summary>
    public ProcedureKind Kind { get; }

    /// <summary>Gets the preferred encoding, if specified.</summary>
    public string? Encoding { get; }

    /// <summary>Gets the list of aliases that resolve to this procedure.</summary>
    public ImmutableArray<string> Aliases { get; }

    /// <summary>Gets the fully qualified procedure name in the form service::name.</summary>
    public string FullName => $"{Service}::{Name}";

    internal static Result<ProcedureSpec> Create(
        string name,
        IReadOnlyList<string>? aliases,
        Func<ImmutableArray<string>, ProcedureSpec> factory)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Err<ProcedureSpec>(ProcedureErrors.NameRequired());
        }

        var validatedAliases = ValidateAliases(aliases);
        if (validatedAliases.IsFailure)
        {
            return Err<ProcedureSpec>(validatedAliases.Error);
        }

        return Ok(factory(validatedAliases.Value));
    }
    internal static Result<ImmutableArray<string>> ValidateAliases(IReadOnlyList<string>? aliases)
    {
        if (aliases is null)
        {
            return Ok(ImmutableArray<string>.Empty);
        }

        var builder = ImmutableArray.CreateBuilder<string>(aliases.Count);
        foreach (var alias in aliases)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                return Err<ImmutableArray<string>>(ProcedureErrors.AliasInvalid());
            }

            builder.Add(alias.Trim());
        }

        return Ok(builder.ToImmutable());
    }
}

/// <summary>Descriptor for a unary procedure.</summary>
public sealed record UnaryProcedureSpec : ProcedureSpec
{
    public UnaryProcedureSpec(
        string service,
        string name,
        UnaryInboundHandler handler,
        string? encoding = null,
        IReadOnlyList<IUnaryInboundMiddleware>? middleware = null,
        IReadOnlyList<string>? aliases = null)
        : base(service, name, ProcedureKind.Unary, encoding, aliases)
    {
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        Middleware = middleware ?? [];
    }

    public UnaryInboundHandler Handler { get; }

    public IReadOnlyList<IUnaryInboundMiddleware> Middleware { get; }
}

/// <summary>Descriptor for a oneway procedure.</summary>
public sealed record OnewayProcedureSpec : ProcedureSpec
{
    public OnewayProcedureSpec(
        string service,
        string name,
        OnewayInboundHandler handler,
        string? encoding = null,
        IReadOnlyList<IOnewayInboundMiddleware>? middleware = null,
        IReadOnlyList<string>? aliases = null)
        : base(service, name, ProcedureKind.Oneway, encoding, aliases)
    {
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        Middleware = middleware ?? [];
    }

    public OnewayInboundHandler Handler { get; }

    public IReadOnlyList<IOnewayInboundMiddleware> Middleware { get; }
}

/// <summary>Descriptor for a server-streaming procedure.</summary>
public sealed record StreamProcedureSpec : ProcedureSpec
{
    public StreamProcedureSpec(
        string service,
        string name,
        StreamInboundHandler handler,
        string? encoding = null,
        IReadOnlyList<IStreamInboundMiddleware>? middleware = null,
        StreamIntrospectionMetadata? metadata = null,
        IReadOnlyList<string>? aliases = null)
        : base(service, name, ProcedureKind.Stream, encoding, aliases)
    {
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        Middleware = middleware ?? [];
        Metadata = metadata ?? StreamIntrospectionMetadata.Default;
    }

    public StreamInboundHandler Handler { get; }

    public IReadOnlyList<IStreamInboundMiddleware> Middleware { get; }

    public StreamIntrospectionMetadata Metadata { get; }
}

/// <summary>Descriptor for a client-streaming procedure.</summary>
public sealed record ClientStreamProcedureSpec : ProcedureSpec
{
    public ClientStreamProcedureSpec(
        string service,
        string name,
        ClientStreamInboundHandler handler,
        string? encoding = null,
        IReadOnlyList<IClientStreamInboundMiddleware>? middleware = null,
        ClientStreamIntrospectionMetadata? metadata = null,
        IReadOnlyList<string>? aliases = null)
        : base(service, name, ProcedureKind.ClientStream, encoding, aliases)
    {
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        Middleware = middleware ?? [];
        Metadata = metadata ?? ClientStreamIntrospectionMetadata.Default;
    }

    public ClientStreamInboundHandler Handler { get; }

    public IReadOnlyList<IClientStreamInboundMiddleware> Middleware { get; }

    public ClientStreamIntrospectionMetadata Metadata { get; }
}

/// <summary>Descriptor for a duplex-streaming procedure.</summary>
public sealed record DuplexProcedureSpec : ProcedureSpec
{
    public DuplexProcedureSpec(
        string service,
        string name,
        DuplexInboundHandler handler,
        string? encoding = null,
        IReadOnlyList<IDuplexInboundMiddleware>? middleware = null,
        DuplexIntrospectionMetadata? metadata = null,
        IReadOnlyList<string>? aliases = null)
        : base(service, name, ProcedureKind.Duplex, encoding, aliases)
    {
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        Middleware = middleware ?? [];
        Metadata = metadata ?? DuplexIntrospectionMetadata.Default;
    }

    public DuplexInboundHandler Handler { get; }

    public IReadOnlyList<IDuplexInboundMiddleware> Middleware { get; }

    public DuplexIntrospectionMetadata Metadata { get; }
}
