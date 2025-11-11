using System.Collections.Immutable;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;

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
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Procedure name cannot be null or whitespace.", nameof(name));
        }

        Name = name;
        Kind = kind;
        Encoding = encoding;
        Aliases = aliases is null
            ? []
            : [.. aliases];

        if (Aliases.Any(static alias => string.IsNullOrWhiteSpace(alias)))
        {
            throw new ArgumentException("Aliases cannot contain null or whitespace entries.", nameof(aliases));
        }
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
}

/// <summary>Descriptor for a unary procedure.</summary>
public sealed record UnaryProcedureSpec : ProcedureSpec
{
    public UnaryProcedureSpec(
        string service,
        string name,
        UnaryInboundDelegate handler,
        string? encoding = null,
        IReadOnlyList<IUnaryInboundMiddleware>? middleware = null,
        IReadOnlyList<string>? aliases = null)
        : base(service, name, ProcedureKind.Unary, encoding, aliases)
    {
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        Middleware = middleware ?? [];
    }

    public UnaryInboundDelegate Handler { get; }

    public IReadOnlyList<IUnaryInboundMiddleware> Middleware { get; }
}

/// <summary>Descriptor for a oneway procedure.</summary>
public sealed record OnewayProcedureSpec : ProcedureSpec
{
    public OnewayProcedureSpec(
        string service,
        string name,
        OnewayInboundDelegate handler,
        string? encoding = null,
        IReadOnlyList<IOnewayInboundMiddleware>? middleware = null,
        IReadOnlyList<string>? aliases = null)
        : base(service, name, ProcedureKind.Oneway, encoding, aliases)
    {
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        Middleware = middleware ?? [];
    }

    public OnewayInboundDelegate Handler { get; }

    public IReadOnlyList<IOnewayInboundMiddleware> Middleware { get; }
}

/// <summary>Descriptor for a server-streaming procedure.</summary>
public sealed record StreamProcedureSpec : ProcedureSpec
{
    public StreamProcedureSpec(
        string service,
        string name,
        StreamInboundDelegate handler,
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

    public StreamInboundDelegate Handler { get; }

    public IReadOnlyList<IStreamInboundMiddleware> Middleware { get; }

    public StreamIntrospectionMetadata Metadata { get; }
}

/// <summary>Descriptor for a client-streaming procedure.</summary>
public sealed record ClientStreamProcedureSpec : ProcedureSpec
{
    public ClientStreamProcedureSpec(
        string service,
        string name,
        ClientStreamInboundDelegate handler,
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

    public ClientStreamInboundDelegate Handler { get; }

    public IReadOnlyList<IClientStreamInboundMiddleware> Middleware { get; }

    public ClientStreamIntrospectionMetadata Metadata { get; }
}

/// <summary>Descriptor for a duplex-streaming procedure.</summary>
public sealed record DuplexProcedureSpec : ProcedureSpec
{
    public DuplexProcedureSpec(
        string service,
        string name,
        DuplexInboundDelegate handler,
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

    public DuplexInboundDelegate Handler { get; }

    public IReadOnlyList<IDuplexInboundMiddleware> Middleware { get; }

    public DuplexIntrospectionMetadata Metadata { get; }
}
