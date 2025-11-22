using System.Collections.Immutable;
using OmniRelay.Core;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Snapshot of dispatcher configuration and runtime bindings for diagnostics and introspection endpoints.
/// </summary>
public sealed partial record DispatcherIntrospection(
    string Service,
    DispatcherStatus Status,
    ProcedureGroups Procedures,
    ImmutableArray<LifecycleComponentDescriptor> Components,
    ImmutableArray<OutboundDescriptor> Outbounds,
    MiddlewareSummary Middleware,
    DeploymentMode Mode = DeploymentMode.InProc,
    ImmutableArray<string> Capabilities = default)
{
    public string Service { get; init; } = Service;

    public DispatcherStatus Status { get; init; } = Status;

    public DeploymentMode Mode { get; init; } = Mode;

    public ImmutableArray<string> Capabilities { get; init; } = Capabilities.IsDefault ? ImmutableArray<string>.Empty : Capabilities;

    public ProcedureGroups Procedures { get; init; } = Procedures;

    public ImmutableArray<LifecycleComponentDescriptor> Components { get; init; } = Components.IsDefault ? ImmutableArray<LifecycleComponentDescriptor>.Empty : Components;

    public ImmutableArray<OutboundDescriptor> Outbounds { get; init; } = Outbounds.IsDefault ? ImmutableArray<OutboundDescriptor>.Empty : Outbounds;

    public MiddlewareSummary Middleware { get; init; } = Middleware;
}

/// <summary>Groups of registered procedures by RPC shape.</summary>
public sealed record ProcedureGroups(
    ImmutableArray<ProcedureDescriptor> Unary,
    ImmutableArray<ProcedureDescriptor> Oneway,
    ImmutableArray<StreamProcedureDescriptor> Stream,
    ImmutableArray<ClientStreamProcedureDescriptor> ClientStream,
    ImmutableArray<DuplexProcedureDescriptor> Duplex)
{
    public static ProcedureGroups Empty { get; } = new(
        ImmutableArray<ProcedureDescriptor>.Empty,
        ImmutableArray<ProcedureDescriptor>.Empty,
        ImmutableArray<StreamProcedureDescriptor>.Empty,
        ImmutableArray<ClientStreamProcedureDescriptor>.Empty,
        ImmutableArray<DuplexProcedureDescriptor>.Empty);

    public ImmutableArray<ProcedureDescriptor> Unary { get; init; } = Unary.IsDefault ? ImmutableArray<ProcedureDescriptor>.Empty : Unary;

    public ImmutableArray<ProcedureDescriptor> Oneway { get; init; } = Oneway.IsDefault ? ImmutableArray<ProcedureDescriptor>.Empty : Oneway;

    public ImmutableArray<StreamProcedureDescriptor> Stream { get; init; } = Stream.IsDefault ? ImmutableArray<StreamProcedureDescriptor>.Empty : Stream;

    public ImmutableArray<ClientStreamProcedureDescriptor> ClientStream { get; init; } = ClientStream.IsDefault ? ImmutableArray<ClientStreamProcedureDescriptor>.Empty : ClientStream;

    public ImmutableArray<DuplexProcedureDescriptor> Duplex { get; init; } = Duplex.IsDefault ? ImmutableArray<DuplexProcedureDescriptor>.Empty : Duplex;
}

/// <summary>Basic procedure info including name, encoding, and aliases.</summary>
public sealed record ProcedureDescriptor(string Name, string? Encoding, ImmutableArray<string> Aliases)
{
    public string Name { get; init; } = Name;

    public string? Encoding { get; init; } = Encoding;

    public ImmutableArray<string> Aliases { get; init; } = Aliases;
}

/// <summary>Server-stream procedure descriptor with response metadata.</summary>
public sealed record StreamProcedureDescriptor(string Name, string? Encoding, ImmutableArray<string> Aliases, StreamIntrospectionMetadata Metadata)
{
    public string Name { get; init; } = Name;

    public string? Encoding { get; init; } = Encoding;

    public ImmutableArray<string> Aliases { get; init; } = Aliases;

    public StreamIntrospectionMetadata Metadata { get; init; } = Metadata;
}

/// <summary>Client-stream procedure descriptor with request metadata.</summary>
public sealed record ClientStreamProcedureDescriptor(string Name, string? Encoding, ImmutableArray<string> Aliases, ClientStreamIntrospectionMetadata Metadata)
{
    public string Name { get; init; } = Name;

    public string? Encoding { get; init; } = Encoding;

    public ImmutableArray<string> Aliases { get; init; } = Aliases;

    public ClientStreamIntrospectionMetadata Metadata { get; init; } = Metadata;
}

/// <summary>Duplex-stream procedure descriptor with channel metadata.</summary>
public sealed record DuplexProcedureDescriptor(string Name, string? Encoding, ImmutableArray<string> Aliases, DuplexIntrospectionMetadata Metadata)
{
    public string Name { get; init; } = Name;

    public string? Encoding { get; init; } = Encoding;

    public ImmutableArray<string> Aliases { get; init; } = Aliases;

    public DuplexIntrospectionMetadata Metadata { get; init; } = Metadata;
}

/// <summary>Lifecycle component descriptor including name, implementation type, and dependencies.</summary>
public sealed record LifecycleComponentDescriptor(string Name, string ComponentType, ImmutableArray<string> Dependencies)
{
    public string Name { get; init; } = Name;

    public string ComponentType { get; init; } = ComponentType;

    public ImmutableArray<string> Dependencies { get; init; } = Dependencies;
}

/// <summary>Outbound binding descriptor lists transports per RPC shape for a service.</summary>
public sealed record OutboundDescriptor(
    string Service,
    ImmutableArray<OutboundBindingDescriptor> Unary,
    ImmutableArray<OutboundBindingDescriptor> Oneway,
    ImmutableArray<OutboundBindingDescriptor> Stream,
    ImmutableArray<OutboundBindingDescriptor> ClientStream,
    ImmutableArray<OutboundBindingDescriptor> Duplex)
{
    public string Service { get; init; } = Service;

    public ImmutableArray<OutboundBindingDescriptor> Unary { get; init; } = Unary;

    public ImmutableArray<OutboundBindingDescriptor> Oneway { get; init; } = Oneway;

    public ImmutableArray<OutboundBindingDescriptor> Stream { get; init; } = Stream;

    public ImmutableArray<OutboundBindingDescriptor> ClientStream { get; init; } = ClientStream;

    public ImmutableArray<OutboundBindingDescriptor> Duplex { get; init; } = Duplex;
}

/// <summary>Outbound transport binding including key, implementation type, and metadata.</summary>
public sealed record OutboundBindingDescriptor(string Key, string ImplementationType, object? Metadata)
{
    public string Key { get; init; } = Key;

    public string ImplementationType { get; init; } = ImplementationType;

    public object? Metadata { get; init; } = Metadata;
}

/// <summary>Lists inbound and outbound middleware types by RPC shape.</summary>
public sealed record MiddlewareSummary(
    ImmutableArray<string> InboundUnary,
    ImmutableArray<string> InboundOneway,
    ImmutableArray<string> InboundStream,
    ImmutableArray<string> InboundClientStream,
    ImmutableArray<string> InboundDuplex,
    ImmutableArray<string> OutboundUnary,
    ImmutableArray<string> OutboundOneway,
    ImmutableArray<string> OutboundStream,
    ImmutableArray<string> OutboundClientStream,
    ImmutableArray<string> OutboundDuplex)
{
    public static MiddlewareSummary Empty { get; } = new(
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty);
    public ImmutableArray<string> InboundUnary { get; init; } = InboundUnary.IsDefault ? ImmutableArray<string>.Empty : InboundUnary;

    public ImmutableArray<string> InboundOneway { get; init; } = InboundOneway.IsDefault ? ImmutableArray<string>.Empty : InboundOneway;

    public ImmutableArray<string> InboundStream { get; init; } = InboundStream.IsDefault ? ImmutableArray<string>.Empty : InboundStream;

    public ImmutableArray<string> InboundClientStream { get; init; } = InboundClientStream.IsDefault ? ImmutableArray<string>.Empty : InboundClientStream;

    public ImmutableArray<string> InboundDuplex { get; init; } = InboundDuplex.IsDefault ? ImmutableArray<string>.Empty : InboundDuplex;

    public ImmutableArray<string> OutboundUnary { get; init; } = OutboundUnary.IsDefault ? ImmutableArray<string>.Empty : OutboundUnary;

    public ImmutableArray<string> OutboundOneway { get; init; } = OutboundOneway.IsDefault ? ImmutableArray<string>.Empty : OutboundOneway;

    public ImmutableArray<string> OutboundStream { get; init; } = OutboundStream.IsDefault ? ImmutableArray<string>.Empty : OutboundStream;

    public ImmutableArray<string> OutboundClientStream { get; init; } = OutboundClientStream.IsDefault ? ImmutableArray<string>.Empty : OutboundClientStream;

    public ImmutableArray<string> OutboundDuplex { get; init; } = OutboundDuplex.IsDefault ? ImmutableArray<string>.Empty : OutboundDuplex;
}

public sealed partial record DispatcherIntrospection
{
    public DispatcherIntrospection Normalize() =>
        this with
        {
            Procedures = Procedures ?? ProcedureGroups.Empty,
            Components = Components.IsDefault ? ImmutableArray<LifecycleComponentDescriptor>.Empty : Components,
            Outbounds = Outbounds.IsDefault ? ImmutableArray<OutboundDescriptor>.Empty : Outbounds,
            Middleware = Middleware ?? MiddlewareSummary.Empty,
            Capabilities = Capabilities.IsDefault ? ImmutableArray<string>.Empty : Capabilities
        };
}
