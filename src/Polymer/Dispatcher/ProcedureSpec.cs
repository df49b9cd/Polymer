using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Polymer.Core.Middleware;
using Polymer.Core.Transport;

namespace Polymer.Dispatcher;

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
            : ImmutableArray.CreateRange(aliases);

        if (Aliases.Any(static alias => string.IsNullOrWhiteSpace(alias)))
        {
            throw new ArgumentException("Aliases cannot contain null or whitespace entries.", nameof(aliases));
        }
    }

    public string Service { get; }
    public string Name { get; }
    public ProcedureKind Kind { get; }
    public string? Encoding { get; }
    public ImmutableArray<string> Aliases { get; }
    public string FullName => $"{Service}::{Name}";
}

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
        Middleware = middleware ?? Array.Empty<IUnaryInboundMiddleware>();
    }

    public UnaryInboundDelegate Handler { get; }
    public IReadOnlyList<IUnaryInboundMiddleware> Middleware { get; }
}

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
        Middleware = middleware ?? Array.Empty<IOnewayInboundMiddleware>();
    }

    public OnewayInboundDelegate Handler { get; }
    public IReadOnlyList<IOnewayInboundMiddleware> Middleware { get; }
}

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
        Middleware = middleware ?? Array.Empty<IStreamInboundMiddleware>();
        Metadata = metadata ?? StreamIntrospectionMetadata.Default;
    }

    public StreamInboundDelegate Handler { get; }
    public IReadOnlyList<IStreamInboundMiddleware> Middleware { get; }
    public StreamIntrospectionMetadata Metadata { get; }
}

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
        Middleware = middleware ?? Array.Empty<IClientStreamInboundMiddleware>();
        Metadata = metadata ?? ClientStreamIntrospectionMetadata.Default;
    }

    public ClientStreamInboundDelegate Handler { get; }
    public IReadOnlyList<IClientStreamInboundMiddleware> Middleware { get; }
    public ClientStreamIntrospectionMetadata Metadata { get; }
}

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
        Middleware = middleware ?? Array.Empty<IDuplexInboundMiddleware>();
        Metadata = metadata ?? DuplexIntrospectionMetadata.Default;
    }

    public DuplexInboundDelegate Handler { get; }
    public IReadOnlyList<IDuplexInboundMiddleware> Middleware { get; }
    public DuplexIntrospectionMetadata Metadata { get; }
}
